﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Rebus.Bus;
using Rebus.Extensions;
using Rebus.Logging;
using Rebus.Threading;
using Rebus.Time;

#pragma warning disable 1998

namespace Rebus.Retry.ErrorTracking
{
    /// <summary>
    /// Implementation of <see cref="IErrorTracker"/> that tracks errors in an in-mem dictionary
    /// </summary>
    public class InMemErrorTracker : IErrorTracker, IInitializable, IDisposable
    {
        const string BackgroundTaskName = "CleanupTrackedErrors";

        readonly ILog _log;
        readonly int _maxDeliveryAttempts;
        readonly ConcurrentDictionary<string, ErrorTracking> _trackedErrors = new ConcurrentDictionary<string, ErrorTracking>();
        readonly IAsyncTask _cleanupOldTrackedErrorsTask;

        bool _disposed;

        /// <summary>
        /// Constructs the in-mem error tracker with the configured number of delivery attempts as the MAX
        /// </summary>
        public InMemErrorTracker(int maxDeliveryAttempts, IRebusLoggerFactory rebusLoggerFactory, IAsyncTaskFactory asyncTaskFactory)
        {
            if (rebusLoggerFactory == null) throw new ArgumentNullException(nameof(rebusLoggerFactory));
            if (asyncTaskFactory == null) throw new ArgumentNullException(nameof(asyncTaskFactory));
            _maxDeliveryAttempts = maxDeliveryAttempts;
            _log = rebusLoggerFactory.GetLogger<InMemErrorTracker>();
            _cleanupOldTrackedErrorsTask = asyncTaskFactory.Create(BackgroundTaskName, CleanupOldTrackedErrors, intervalSeconds: 60);
        }

        /// <summary>
        /// Initializes the in-mem error tracker - starts a background task that periodically cleans up tracked errors that haven't had any activity for 10 minutes or more
        /// </summary>
        public void Initialize() => _cleanupOldTrackedErrorsTask.Start();

        /// <summary>
        /// Registers the given <paramref name="exception"/> under the supplied <paramref name="messageId"/>
        /// </summary>
        public void RegisterError(string messageId, Exception exception, bool final = false)
        {
            var errorTracking = _trackedErrors.AddOrUpdate(messageId,
                id => new ErrorTracking(exception, final),
                (id, tracking) => tracking.AddError(exception, final));

            var message = final
                ? "Unhandled exception {errorNumber} (FINAL) while handling message with ID {messageId}"
                : "Unhandled exception {errorNumber} while handling message with ID {messageId}";

            _log.Warn(exception, message, errorTracking.Errors.Count(), messageId);
        }

        /// <summary>
        /// Gets whether too many errors have been tracked for the given <paramref name="messageId"/>
        /// </summary>
        public bool HasFailedTooManyTimes(string messageId)
        {
            var hasTrackingForThisMessage = _trackedErrors.TryGetValue(messageId, out var existingTracking);
            if (!hasTrackingForThisMessage) return false;

            var hasFailedTooManyTimes = existingTracking.Final
                                        || existingTracking.ErrorCount >= _maxDeliveryAttempts;

            return hasFailedTooManyTimes;
        }

        /// <summary>
        /// Gets a short description of the tracked errors for the given <paramref name="messageId"/> on the form
        /// "n unhandled exceptions"
        /// </summary>
        public string GetShortErrorDescription(string messageId)
        {
            return _trackedErrors.TryGetValue(messageId, out var errorTracking)
                ? $"{errorTracking.Errors.Count()} unhandled exceptions"
                : null;
        }

        /// <summary>
        /// Gets a long and detailed description of the tracked errors for the given <paramref name="messageId"/>
        /// consisting of time and full exception details for all registered exceptions
        /// </summary>
        public string GetFullErrorDescription(string messageId)
        {
            if (!_trackedErrors.TryGetValue(messageId, out var errorTracking))
            {
                return null;
            }

            var fullExceptionInfo = string.Join(Environment.NewLine, errorTracking.Errors.Select(e =>
                $"{e.Time}: {e.Exception}"));

            return $"{errorTracking.Errors.Count()} unhandled exceptions: {fullExceptionInfo}";
        }

        /// <summary>
        /// Gets all caught exceptions for the message ID
        /// </summary>
        public IEnumerable<Exception> GetExceptions(string messageId)
        {
            if (!_trackedErrors.TryGetValue(messageId, out var errorTracking))
            {
                return Enumerable.Empty<Exception>();
            }

            return errorTracking.Errors
                .Select(e => e.Exception)
                .ToList();
        }

        /// <summary>
        /// Cleans up whichever tracking wr have done for the given <paramref name="messageId"/>
        /// </summary>
        public void CleanUp(string messageId)
        {
            ErrorTracking dummy;
            _trackedErrors.TryRemove(messageId, out dummy);
        }

        async Task CleanupOldTrackedErrors()
        {
            ErrorTracking _;

            _trackedErrors
                .ToList()
                .Where(e => e.Value.ElapsedSinceLastError > TimeSpan.FromMinutes(10))
                .ForEach(tracking => _trackedErrors.TryRemove(tracking.Key, out _));
        }

        class ErrorTracking
        {
            readonly CaughtException[] _caughtExceptions;

            ErrorTracking(IEnumerable<CaughtException> caughtExceptions, bool final)
            {
                Final = final;
                _caughtExceptions = caughtExceptions.ToArray();
            }

            public ErrorTracking(Exception exception, bool final)
                : this(new[] { new CaughtException(exception) }, final)
            {
            }

            public int ErrorCount => _caughtExceptions.Length;

            public bool Final { get; }

            public IEnumerable<CaughtException> Errors => _caughtExceptions;

            public ErrorTracking AddError(Exception caughtException, bool final)
            {
                // don't change anymore if this one is already final
                if (Final) return this;

                return new ErrorTracking(_caughtExceptions.Concat(new[] { new CaughtException(caughtException) }), final);
            }

            public TimeSpan ElapsedSinceLastError
            {
                get
                {
                    var timeOfMostRecentError = _caughtExceptions.Max(e => e.Time);
                    var elapsedSinceLastError = timeOfMostRecentError.ElapsedUntilNow();
                    return elapsedSinceLastError;
                }
            }
        }

        class CaughtException
        {
            public CaughtException(Exception exception)
            {
                Exception = exception ?? throw new ArgumentNullException(nameof(exception));
                Time = RebusTime.Now;
            }

            public Exception Exception { get; }
            public DateTimeOffset Time { get; }
        }

        /// <summary>
        /// Stops the periodic cleanup of tracked messages
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                _cleanupOldTrackedErrorsTask.Dispose();
            }
            finally
            {
                _disposed = true;
            }
        }
    }
}