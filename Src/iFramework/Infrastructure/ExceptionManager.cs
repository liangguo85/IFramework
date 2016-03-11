﻿
using IFramework.Infrastructure.Logging;
using IFramework.SysExceptions;
using IFramework.SysExceptions.ErrorCodes;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;

namespace IFramework.Infrastructure
{
    public class ApiResult
    {
        public bool success { get; set; }
        public object errorCode { get; set; }
        public string message { get; set; }

        public ApiResult()
        {
            success = true;
            errorCode = 0;
        }

        public ApiResult(object errorCode, string message = null)
        {
            this.errorCode = errorCode;
            this.message = message;
            success = false;
        }

    }

    public class ApiResult<TResult> : ApiResult
    {
        public TResult result { get; set; }

        public ApiResult()
        {
            success = true;
        }
        public ApiResult(TResult result)
            : this()
        {
            this.result = result;
        }

        public ApiResult(object errorCode, string message = null)
            : base(errorCode, message)
        {

        }
    }

    public static class ExceptionManager
    {
        static ILogger _logger = IoCFactory.Resolve<ILoggerFactory>().Create(typeof(ExceptionManager));
        public async static Task<ApiResult<T>> ProcessAsync<T>(Func<Task<T>> func, bool continueOnCapturedContext = false, bool needRetry = false)
        {
            ApiResult<T> apiResult = null;
            do
            {
                try
                {
                    var t = await func().ConfigureAwait(continueOnCapturedContext);
                    needRetry = false;
                    apiResult = new ApiResult<T>(t);
                }
                catch (Exception ex)
                {
                    if (!(ex is OptimisticConcurrencyException) || !needRetry)
                    {
                        var baseException = ex.GetBaseException();
                        if (baseException is SysException)
                        {
                            var sysException = baseException as SysException;
                            apiResult = new ApiResult<T>(sysException.ErrorCode, sysException.Message);
                        }
                        else
                        {
                            apiResult = new ApiResult<T>(ErrorCode.UnknownError, baseException.Message);
                            _logger.Error(ex);
                        }
                        needRetry = false;
                    }
                }
            } while (needRetry);

            return apiResult;
        }

        public async static Task<ApiResult> ProcessAsync(Func<Task> func, bool continueOnCapturedContext = false, bool needRetry = false)
        {
            ApiResult apiResult = null;
            do
            {
                try
                {
                    await func().ConfigureAwait(continueOnCapturedContext);
                    needRetry = false;
                    apiResult = new ApiResult();
                }
                catch (Exception ex)
                {
                    if (!(ex is OptimisticConcurrencyException) || !needRetry)
                    {
                        var baseException = ex.GetBaseException();
                        if (baseException is SysException)
                        {
                            var sysException = baseException as SysException;
                            apiResult = new ApiResult(sysException.ErrorCode, sysException.Message);
                        }
                        else
                        {
                            apiResult = new ApiResult(ErrorCode.UnknownError, baseException.Message);
                            _logger.Error(ex);
                        }
                        needRetry = false;
                    }
                }
            } while (needRetry);

            return apiResult;
        }

        public static ApiResult Process(Action action, bool needRetry = false)
        {
            ApiResult apiResult = null;
            do
            {
                try
                {
                    action();
                    apiResult = new ApiResult();
                    needRetry = false;
                }
                catch (Exception ex)
                {
                    if (!(ex is OptimisticConcurrencyException) || !needRetry)
                    {
                        var baseException = ex.GetBaseException();
                        if (baseException is SysException)
                        {
                            var sysException = baseException as SysException;
                            apiResult = new ApiResult(sysException.ErrorCode, sysException.Message);
                        }
                        else
                        {
                            apiResult = new ApiResult(ErrorCode.UnknownError, baseException.Message);
                            _logger.Error(ex);
                        }
                        needRetry = false;
                    }
                }
            }
            while (needRetry);
            return apiResult;
        }

        public static ApiResult<T> Process<T>(Func<T> func, bool needRetry = false)
        {
            ApiResult<T> apiResult = null;
            do
            {
                try
                {
                    var result = func();
                    needRetry = false;
                    if (result != null)
                    {
                        apiResult = new ApiResult<T>(result);
                    }
                    else
                    {
                        apiResult = new ApiResult<T>();
                    }
                }
                catch (Exception ex)
                {
                    if (!(ex is OptimisticConcurrencyException) || !needRetry)
                    {
                        var baseException = ex.GetBaseException();
                        if (baseException is SysException)
                        {
                            var sysException = baseException as SysException;
                            apiResult = new ApiResult<T>(sysException.ErrorCode, sysException.Message);
                        }
                        else
                        {
                            apiResult = new ApiResult<T>(ErrorCode.UnknownError, baseException.Message);
                            _logger.Error(ex);
                        }
                        needRetry = false;
                    }
                }
            }
            while (needRetry);
            return apiResult;
        }
    }
}