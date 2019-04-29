﻿namespace Amazon.SecretsManager.Extensions.Caching
{
    using Amazon.Runtime;
    using Amazon.SecretsManager.Model;
    using Amazon.Util;
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    public abstract class SecretCacheObject<T>
    {
        /// The number of milliseconds to wait after an exception. 
        private const long EXCEPTION_BACKOFF = 1000;

        /// The growth factor of the backoff duration. 
        private const long EXCEPTION_BACKOFF_GROWTH_FACTOR = 2;
        
        /// The maximum number of milliseconds to wait before retrying a failed
        /// request.
        private const long BACKOFF_PLATEAU = 128 * EXCEPTION_BACKOFF;

        private JitteredDelay EXCEPTION_JITTERED_DELAY = new JitteredDelay(TimeSpan.FromMilliseconds(EXCEPTION_BACKOFF), 
                                                                    TimeSpan.FromMilliseconds(EXCEPTION_BACKOFF), 
                                                                    TimeSpan.FromMilliseconds(BACKOFF_PLATEAU));
        
        /// When forcing a refresh using the refreshNow method, a random sleep
        /// will be performed using this value.  This helps prevent code from
        /// executing a refreshNow in a continuous loop without waiting.
        private const long FORCE_REFRESH_JITTER_BASE_INCREMENT = 3500;
        private const long FORCE_REFRESH_JITTER_VARIANCE = 1000;

        private JitteredDelay FORCE_REFRESH_JITTERED_DELAY = new JitteredDelay(TimeSpan.FromMilliseconds(FORCE_REFRESH_JITTER_BASE_INCREMENT),
                                                                        TimeSpan.FromMilliseconds(FORCE_REFRESH_JITTER_VARIANCE));

        /// The secret identifier for this cached object. 
        protected String secretId;

        /// A private object to synchronize access to certain methods. 
        protected static readonly SemaphoreSlim Lock = new SemaphoreSlim(1,1);

        /// The AWS Secrets Manager client to use for requesting secrets. 
        protected IAmazonSecretsManager client;

        /// The Secret Cache Configuration.
        protected SecretCacheConfiguration config;

        /// A flag to indicate a refresh is needed. 
        private bool refreshNeeded = true;

        /// The result of the last AWS Secrets Manager request for this item.
        private Object data = null;
        
        /// If the last request to AWS Secrets Manager resulted in an exception,
        /// that exception will be thrown back to the caller when requesting
        /// secret data.
        protected Exception exception = null;

       
        /// The number of exceptions encountered since the last successfully
        /// AWS Secrets Manager request.  This is used to calculate an exponential
        /// backoff.
        private long exceptionCount = 0;
        
        /// The time to wait before retrying a failed AWS Secrets Manager request.
        private long nextRetryTime = 0;

        public static readonly ThreadLocal<Random> random = new ThreadLocal<Random>(() => new Random(Environment.TickCount));



        /// <summary>
        /// Construct a new cached item for the secret.
        /// </summary>
        /// <param name="secretId"> The secret identifier. This identifier could be the full ARN or the friendly name for the secret. </param>
        /// <param name="client"> The AWS Secrets Manager client to use for requesting the secret. </param>
        /// <param name="config"> The secret cache configuration. </param>
        public SecretCacheObject(String secretId, IAmazonSecretsManager client, SecretCacheConfiguration config)
        {
            this.secretId = secretId;
            this.client = client;
            this.config = config;
        }
     
        protected abstract Task<T> ExecuteRefreshAsync();

        protected abstract Task<GetSecretValueResponse> GetSecretValueAsync(T result);

        /// <summary>
        /// Return the typed result object.
        /// </summary>
        private T GetResult()
        {
            if (null != config.CacheHook)
            {
                return (T)config.CacheHook.Get(data);
            }
            return (T)data;
        }

        /// <summary>
        /// Store the result data.
        /// </summary>
        private void SetResult(T result)
        {
            if (null != config.CacheHook)
            {
                data = config.CacheHook.Put(result);
            }
            else
            {
                data = result;
            }
        }

        /// <summary>
        /// Determine if the secret object should be refreshed.
        /// </summary>
        protected bool IsRefreshNeeded()
        {
            if (refreshNeeded) { return true; }
            if (null == exception) { return false; }

            // If we encountered an exception on the last attempt
            // we do not want to keep retrying without a pause between
            // the refresh attempts.
            //
            // If we have exceeded our backoff time we will refresh
            // the secret now.
            return Environment.TickCount >= nextRetryTime;
        }

        /// <summary>
        /// Refresh the cached secret state only when needed.
        /// </summary>
        private async Task<bool> RefreshAsync()
        {
            if (!IsRefreshNeeded()) { return false; }
            refreshNeeded = false;
            try
            {
                SetResult(await ExecuteRefreshAsync());
                exception = null;
                exceptionCount = 0;
                return true;
            }
            catch (Exception ex) when (ex is AmazonServiceException || ex is AmazonClientException)
            {
                exception = ex;
                // Determine the amount of growth in exception backoff time based on the growth
                // factor and default backoff duration.

                nextRetryTime = Environment.TickCount + EXCEPTION_JITTERED_DELAY.GetRetryDelay((int)exceptionCount).Milliseconds;
            }
            return false;
        }

        /// <summary>
        /// Method to force the refresh of a cached secret state.
        /// Returns true if the refresh completed without error.
        /// </summary>
        public async Task<bool> RefreshNowAsync()
        {
            refreshNeeded = true;
            // When forcing a refresh, always sleep with a random jitter
            // to prevent coding errors that could be calling refreshNow
            // in a loop.
            long sleep = FORCE_REFRESH_JITTERED_DELAY.GetRetryDelay(1).Milliseconds;

            // Make sure we are not waiting for the next refresh after an
            // exception.  If we are, sleep based on the retry delay of
            // the refresh to prevent a hard loop in attempting to refresh a
            // secret that continues to throw an exception such as AccessDenied.
            if (null != exception)
            {
                long wait = nextRetryTime - Environment.TickCount;
                sleep = Math.Max(wait, sleep);
            }
            Thread.Sleep((int)sleep);

            // Perform the requested refresh.
            bool success = false;
            await Lock.WaitAsync();
            try
            {
                success = await RefreshAsync();
            }
            finally
            {
                Lock.Release();
            }
            return (null == exception && success);
        }

        /// <summary>
        /// Asynchronously return the cached result from AWS Secrets Manager for GetSecretValue.
        /// If the secret is due for a refresh, the refresh will occur before the result is returned.
        /// If the refresh fails, the cached result is returned, or the cached exception is thrown.
        /// </summary>
        public async Task<GetSecretValueResponse> GetSecretValue()
        {
            bool success = false;
            await Lock.WaitAsync();
            try
            {
                success = await RefreshAsync();
            }
            finally
            {
                Lock.Release();
            }

            if (!success && null == data && null != exception)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
            }
            return await GetSecretValueAsync(GetResult());
        }
    }
}
