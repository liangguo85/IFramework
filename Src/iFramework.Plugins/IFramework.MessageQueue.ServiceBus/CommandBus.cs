﻿using IFramework.Command;
using IFramework.Config;
using IFramework.Infrastructure;
using IFramework.Infrastructure.Logging;
using IFramework.Infrastructure.Unity.LifetimeManagers;
using IFramework.Message;
using IFramework.MessageQueue.ServiceBus.MessageFormat;
using IFramework.SysExceptions;
using IFramework.UnitOfWork;
using Microsoft.ServiceBus.Messaging;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IFramework.MessageQueue.ServiceBus
{
    public class CommandBus : ICommandBus
    {
        protected ICommandHandlerProvider _handlerProvider;
        protected ILinearCommandManager _linearCommandManager;
        protected Hashtable _commandStateQueues;
        protected Task _subscriptionConsumeTask;
        protected Task _sendCommandWorkTask;
        protected string _replyTopicName;
        protected string _replySubscriptionName;
        protected string[] _commandQueueNames;
        protected List<QueueClient> _commandQueueClients;
        protected SubscriptionClient _replySubscriptionClient;
        private BlockingCollection<IMessageContext> _toBeSentCommandQueue;
        protected ServiceBusClient _serviceBusClient;
        volatile bool _exit = false;
        protected bool InProc { get; set; }
        protected List<QueueClient> CommandProducers { get; set; }
        ILogger _logger;
        public CommandBus(ICommandHandlerProvider handlerProvider,
                          ILinearCommandManager linearCommandManager,
                          string serviceBusConnectionString,
                          string[] commandQueueNames,
                          string replyTopicName,
                          string replySubscriptionName)
        {
            _logger = IoCFactory.Resolve<ILoggerFactory>().Create(this.GetType());
            _serviceBusClient = new ServiceBusClient(serviceBusConnectionString);
            _commandStateQueues = Hashtable.Synchronized(new Hashtable());
            _handlerProvider = handlerProvider;
            _linearCommandManager = linearCommandManager;
            _replyTopicName = replyTopicName;
            _replySubscriptionName = replySubscriptionName;
            _commandQueueNames = commandQueueNames;
            _commandQueueClients = new List<QueueClient>();
            _toBeSentCommandQueue = new BlockingCollection<IMessageContext>();
        }

        public void Start()
        {
            #region init sending commands Worker

            #region Init  Command Queue client
            if (_commandQueueNames != null && _commandQueueNames.Length > 0)
            {
                _commandQueueNames.ForEach(commandQueueName =>
                    _commandQueueClients.Add(_serviceBusClient.CreateQueueClient(commandQueueName)));
            }
            #endregion

            _sendCommandWorkTask = Task.Factory.StartNew(() =>
            {
                using (var messageStore = IoCFactory.Resolve<IMessageStore>())
                {
                    messageStore.GetAllUnSentCommands()
                        .ForEach(commandContext => _toBeSentCommandQueue.Add(commandContext));
                }
                while (!_exit)
                {
                    try
                    {
                        var commandContext = _toBeSentCommandQueue.Take();
                        SendCommand(commandContext);
                        Task.Factory.StartNew(() =>
                        {
                            using (var messageStore = IoCFactory.Resolve<IMessageStore>())
                            {
                                messageStore.RemoveSentCommand(commandContext.MessageID);
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug("send command quit", ex);
                    }
                }
            }, TaskCreationOptions.LongRunning);
            #endregion

            #region init process command reply worker

            _replySubscriptionClient = _serviceBusClient.CreateSubscriptionClient(_replyTopicName, _replySubscriptionName);

            _subscriptionConsumeTask = Task.Factory.StartNew(() =>
            {
                while (!_exit)
                {
                    BrokeredMessage brokeredMessage = null;
                    try
                    {
                        brokeredMessage = _replySubscriptionClient.Receive();
                        if (brokeredMessage != null)
                        {
                            var reply = new MessageContext(brokeredMessage);
                            ConsumeReply(reply);
                        }
                    }
                    catch (Exception ex)
                    {
                        Thread.Sleep(1000);
                        _logger.Error("consume reply error", ex);
                    }
                    finally
                    {
                        if (brokeredMessage != null)
                        {
                            brokeredMessage.Complete();
                        }
                    }
                }
            }, TaskCreationOptions.LongRunning);

            #endregion
        }

        public void Stop()
        {
            _exit = true;
            if (_subscriptionConsumeTask != null)
            {
                _replySubscriptionClient.Close();
                if (!_subscriptionConsumeTask.Wait(1000))
                {
                    _logger.ErrorFormat("receiver can't be stopped!");
                }
            }
            if (_sendCommandWorkTask != null)
            {
                _toBeSentCommandQueue.CompleteAdding();
                if (_sendCommandWorkTask.Wait(2000))
                {
                    _sendCommandWorkTask.Dispose();
                }
                else
                {
                    _logger.ErrorFormat(" consumer can't be stopped!");
                }
            }
        }

        public void Send(IEnumerable<IMessageContext> commandContexts)
        {
            commandContexts.ForEach(commandContext => _toBeSentCommandQueue.Add(commandContext));
        }

        protected virtual void SendCommand(IMessageContext commandContext)
        {
            QueueClient commandProducer = null;
            if (_commandQueueClients.Count == 1)
            {
                commandProducer = _commandQueueClients[0];
            }
            else if (_commandQueueClients.Count > 1)
            {
                var commandKey = commandContext.Key;
                int keyUniqueCode = !string.IsNullOrWhiteSpace(commandKey) ?
                    commandKey.GetUniqueCode() : commandContext.MessageID.GetUniqueCode();
                commandProducer = _commandQueueClients[Math.Abs(keyUniqueCode % _commandQueueClients.Count)];
            }
            if (commandProducer == null) return;
            while (true)
            {
                try
                {
                    var brokeredMessage = ((MessageContext)commandContext).BrokeredMessage;

                    commandProducer.Send(brokeredMessage);
                    _logger.InfoFormat("send commandID:{0} length:{1} send status:{2}",
                        commandContext.MessageID, brokeredMessage.Size, brokeredMessage.State);
                    break;
                }
                catch (Exception ex)
                {
                    if (ex is InvalidOperationException)
                    {
                        commandContext = new MessageContext(commandContext.Message as IMessage, commandContext.ReplyToEndPoint, commandContext.Key);
                    }
                    Thread.Sleep(1000);
                }
            }
        }

        protected void ConsumeReply(IMessageContext reply)
        {
            _logger.InfoFormat("Handle reply:{0} content:{1}", reply.MessageID, reply.ToJson());
            var messageState = _commandStateQueues[reply.CorrelationID] as IFramework.Message.MessageState;
            if (messageState != null)
            {
                _commandStateQueues.TryRemove(reply.MessageID);
                if (reply.Message is Exception)
                {
                    messageState.TaskCompletionSource.TrySetException(reply.Message as Exception);
                }
                else
                {
                    messageState.TaskCompletionSource.TrySetResult(reply.Message);
                }
            }
        }

        protected IFramework.Message.MessageState BuildMessageState(IMessageContext messageContext, CancellationToken cancellationToken)
        {
            var pendingRequestsCts = new CancellationTokenSource();
            CancellationTokenSource linkedCts = CancellationTokenSource
                   .CreateLinkedTokenSource(cancellationToken, pendingRequestsCts.Token);
            cancellationToken = linkedCts.Token;
            var source = new TaskCompletionSource<object>();
            var state = new IFramework.Message.MessageState
            {
                MessageID = messageContext.MessageID,
                TaskCompletionSource = source,
                CancellationToken = cancellationToken,
                MessageContext = messageContext
            };
            return state;
        }

        public Task Send(ICommand command)
        {
            return Send(command, CancellationToken.None);
        }

        public Task Send(ICommand command, CancellationToken cancellationToken)
        {
            var currentMessageContext = PerMessageContextLifetimeManager.CurrentMessageContext;
            if (currentMessageContext != null && currentMessageContext.Message is ICommand)
            {
                // A command sent in a CommandContext is not allowed. We throw exception!!!
                throw new NotSupportedException("Command is not allowd to be sent in another command context!");
            }

            string commandKey = null;
            if (command is ILinearCommand)
            {
                var linearKey = _linearCommandManager.GetLinearKey(command as ILinearCommand);
                if (linearKey != null)
                {
                    commandKey = linearKey.ToString();
                }
            }
            IMessageContext commandContext = null;
            commandContext = new MessageContext(command, _replyTopicName, commandKey);
            Task task = null;
            //if (InProc && currentMessageContext == null && !(command is ILinearCommand))
            //{
            //    task = SendInProc(commandContext, cancellationToken);
            //}
            //else
            //{
            // remain this to be compatible for the early version that has no Add Method
            //if (currentMessageContext != null)
            //{
            //    ((MessageContext)currentMessageContext).ToBeSentMessageContexts.Add(commandContext);
            //}
            //else
            // {
            task = SendAsync(commandContext, cancellationToken);
            //}
            //  }
            return task;
        }

        public void Add(ICommand command)
        {
            var currentMessageContext = PerMessageContextLifetimeManager.CurrentMessageContext;
            if (currentMessageContext == null)
            {
                throw new CurrentMessageContextIsNull();
            }
            string commandKey = null;
            if (command is ILinearCommand)
            {
                var linearKey = _linearCommandManager.GetLinearKey(command as ILinearCommand);
                if (linearKey != null)
                {
                    commandKey = linearKey.ToString();
                }
            }
            IMessageContext commandContext = new MessageContext(command, _replyTopicName, commandKey);
            ((MessageContext)currentMessageContext).ToBeSentMessageContexts.Add(commandContext);
        }

        public Task<TResult> Send<TResult>(ICommand command)
        {
            return Send<TResult>(command, CancellationToken.None);
        }

        public Task<TResult> Send<TResult>(ICommand command, CancellationToken cancellationToken)
        {
            return Send(command).ContinueWith<TResult>(t =>
                {
                    if (t.IsFaulted)
                    {
                        throw t.Exception;
                    }
                    else
                    {
                        return (TResult)(t as Task<object>).Result;
                    }
                });
        }

        //protected virtual Task SendInProc(IMessageContext commandContext, CancellationToken cancellationToken)
        //{
        //    Task task = null;
        //    var command = commandContext.Message as ICommand;
        //    if (command is ILinearCommand)
        //    {
        //        task = SendAsync(commandContext, cancellationToken);
        //    }
        //    else if (command != null) //if not a linear command, we run synchronously.
        //    {
        //        task = Task.Factory.StartNew(() =>
        //        {
        //            IMessageStore messageStore = null;
        //            try
        //            {
        //                var needRetry = command.NeedRetry;
        //                object result = null;
        //                PerMessageContextLifetimeManager.CurrentMessageContext = commandContext;
        //                messageStore = IoCFactory.Resolve<IMessageStore>();
        //                if (!messageStore.HasCommandHandled(commandContext.MessageID))
        //                {
        //                    var commandHandler = _handlerProvider.GetHandler(command.GetType());
        //                    if (commandHandler == null)
        //                    {
        //                        PerMessageContextLifetimeManager.CurrentMessageContext = null;
        //                        throw new NoHandlerExists();
        //                    }

        //                    do
        //                    {
        //                        try
        //                        {
        //                            ((dynamic)commandHandler).Handle((dynamic)command);
        //                            result = commandContext.Reply;
        //                            needRetry = false;
        //                        }
        //                        catch (Exception ex)
        //                        {
        //                            if (!(ex is OptimisticConcurrencyException) || !needRetry)
        //                            {
        //                                throw;
        //                            }
        //                        }
        //                    } while (needRetry);
        //                    return result;
        //                }
        //                else
        //                {
        //                    throw new MessageDuplicatelyHandled();
        //                }
        //            }
        //            catch (Exception e)
        //            {
        //                if (e is DomainException)
        //                {
        //                    _logger.Warn(command.ToJson(), e);
        //                }
        //                else
        //                {
        //                    _logger.Error(command.ToJson(), e);
        //                }
        //                if (messageStore != null)
        //                {
        //                    messageStore.SaveFailedCommand(commandContext);
        //                }
        //                throw;
        //            }
        //            finally
        //            {
        //                PerMessageContextLifetimeManager.CurrentMessageContext = null;
        //            }
        //        }, cancellationToken);
        //    }
        //    return task;
        //}

        protected virtual Task SendAsync(IMessageContext commandContext, CancellationToken cancellationToken)
        {
            var commandState = BuildMessageState(commandContext, cancellationToken);
            commandState.CancellationToken.Register(OnCancel, commandState);
            _commandStateQueues.Add(commandState.MessageID, commandState);
            _toBeSentCommandQueue.Add(commandContext, cancellationToken);
            return commandState.TaskCompletionSource.Task;
        }

        protected void OnCancel(object state)
        {
            var messageState = state as IFramework.Message.MessageState;
            if (messageState != null)
            {
                _commandStateQueues.TryRemove(messageState.MessageID);
            }
        }
    }
}
