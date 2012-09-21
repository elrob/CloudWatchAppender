﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Amazon.Runtime;
using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;

namespace CloudWatchAppender
{
    public class CloudWatchAppender : AppenderSkeleton
    {
        public string AccessKey { get; set; }
        public string Secret { get; set; }
        public string EndPoint { get; set; }

        public string Unit { get; set; }
        public string Value { get; set; }
        public string MetricName { get; set; }
        public string Namespace { get; set; }
        public string Timestamp { get; set; }

        public Dimension Dimension { set { AddDimension(value); } }

        private bool _configOverrides = true;
        public bool ConfigOverrides
        {
            get { return _configOverrides; }
            set { _configOverrides = value; }
        }

        private Dictionary<string, Dimension> _dimensions = new Dictionary<string, Dimension>();

        private void AddDimension(Dimension value)
        {
            _dimensions[value.Name] = value;
        }

        public int MaxRequestsPerSecond
        {
            set { _eventCap = new EventCap(value); }
        }

        private static ConcurrentDictionary<int, Task> _tasks = new ConcurrentDictionary<int, Task>();

        private AmazonCloudWatch _client;

        public CloudWatchAppender()
        {
            var hierarchy = ((Hierarchy)log4net.LogManager.GetRepository());
            var logger = hierarchy.GetLogger("Amazon") as Logger;
            logger.Level = Level.Off;

            hierarchy.AddRenderer(typeof(Amazon.CloudWatch.Model.MetricDatum), new MetricDatumRenderer());
        }

        private void SetupClient()
        {
            if (_client != null)
                return;

            AmazonCloudWatchConfig cloudWatchConfig = null;
            RegionEndpoint regionEndpoint = null;

            if (string.IsNullOrEmpty(EndPoint) && ConfigurationManager.AppSettings["AWSServiceEndpoint"] != null)
                EndPoint = ConfigurationManager.AppSettings["AWSServiceEndpoint"];

            if (string.IsNullOrEmpty(AccessKey) && ConfigurationManager.AppSettings["AWSAccessKey"] != null)
                AccessKey = ConfigurationManager.AppSettings["AWSAccessKey"];

            if (string.IsNullOrEmpty(Secret) && ConfigurationManager.AppSettings["AWSSecretKey"] != null)
                Secret = ConfigurationManager.AppSettings["AWSSecretKey"];

            _client = AWSClientFactory.CreateAmazonCloudWatchClient(AccessKey, Secret);

            try
            {

            if (!string.IsNullOrEmpty(EndPoint))
            {
                if (EndPoint.StartsWith("http"))
                {
                    cloudWatchConfig = new AmazonCloudWatchConfig { ServiceURL = EndPoint };
                    _client = AWSClientFactory.CreateAmazonCloudWatchClient(cloudWatchConfig);
                }
                else
                {
                    regionEndpoint = RegionEndpoint.GetBySystemName(EndPoint);
                    _client = AWSClientFactory.CreateAmazonCloudWatchClient(regionEndpoint);
                }
            }
            }
            catch (AmazonServiceException)
            {
            }

            if (!string.IsNullOrEmpty(AccessKey))
                if (regionEndpoint != null)
                    _client = AWSClientFactory.CreateAmazonCloudWatchClient(AccessKey, Secret, regionEndpoint);
                else if (cloudWatchConfig != null)
                    _client = AWSClientFactory.CreateAmazonCloudWatchClient(AccessKey, Secret, cloudWatchConfig);
                else
                    _client = AWSClientFactory.CreateAmazonCloudWatchClient(AccessKey, Secret);

            //Debug
            var metricDatum = new Amazon.CloudWatch.Model.MetricDatum().WithMetricName("CloudWatchAppender").WithValue(1).WithUnit("Count");
            //_client.PutMetricData(new PutMetricDataRequest().WithNamespace("CloudWatchAppender").WithMetricData(metricDatum));
        }



        public static bool HasPendingRequests
        {
            get { return _tasks.Values.Any(t => !t.IsCompleted); }
        }


        public static void WaitForPendingRequests(TimeSpan timeout)
        {
            var startedTime = DateTime.Now;
            var timeConsumed = TimeSpan.Zero;
            while (HasPendingRequests && timeConsumed < timeout)
            {
                Task.WaitAll(_tasks.Values.ToArray(), timeout - timeConsumed);
                timeConsumed = DateTime.Now - startedTime;
            }
        }

        public static void WaitForPendingRequests()
        {
            while (HasPendingRequests)
                Task.WaitAll(_tasks.Values.ToArray());
        }

        protected override void Append(LoggingEvent loggingEvent)
        {
            System.Diagnostics.Debug.WriteLine("Appending");

            if (_eventCap.Request(loggingEvent.TimeStamp))
            {
                System.Diagnostics.Debug.WriteLine("Appending denied due to event saturation.");
                return;
            }

            if (Layout == null)
                Layout = new PatternLayout("%message");

            var renderedString = RenderLoggingEvent(loggingEvent);

            var patternParser = new PatternParser(loggingEvent);

            renderedString = patternParser.Parse(renderedString);

            System.Diagnostics.Debug.WriteLine(string.Format("RenderedString: {0}", renderedString));

            var parsedDimensions =
                _dimensions
                    .Select(x => new Dimension {Name = x.Key, Value = patternParser.Parse(x.Value.Value)}).
                    ToDictionary(x => x.Name, y => y);
             
            var parser = new EventMessageParser(renderedString, ConfigOverrides)
                        {
                            DefaultMetricName = string.IsNullOrEmpty(MetricName)
                                                ? null
                                                : patternParser.Parse(MetricName),
                            DefaultNameSpace = string.IsNullOrEmpty(Namespace)
                                                    ? null
                                                    : patternParser.Parse(Namespace),
                            DefaultUnit = String.IsNullOrEmpty(Unit)
                                                ? null
                                                : patternParser.Parse(Unit),
                            DefaultDimensions = _dimensions.Any()
                                                        ? parsedDimensions
                                                        : null,
                            DefaultTimestamp = string.IsNullOrEmpty(Timestamp)
                                                        ? null
                                                        : (DateTimeOffset?)DateTimeOffset.Parse(patternParser.Parse(Timestamp))
                        };

            if (!string.IsNullOrEmpty(Value) && ConfigOverrides)
                parser.DefaultValue = Double.Parse(Value, CultureInfo.InvariantCulture);

            parser.Parse();

            foreach (var r in parser)
                SendItOff(r);
        }

        private EventCap _eventCap = new EventCap();

        private void SendItOff(PutMetricDataRequest r)
        {
            if (_client == null)
                SetupClient();

            Task task =
                Task.Factory.StartNew(() =>
                {
                    try
                    {
                        var tmpCulture = Thread.CurrentThread.CurrentCulture;
                        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-GB", false);

                        System.Diagnostics.Debug.WriteLine("Sending");
                        _client.PutMetricData(r);

                        Thread.CurrentThread.CurrentCulture = tmpCulture;
                    }
                    catch (AmazonCloudWatchException e)
                    {
                        System.Diagnostics.Debug.WriteLine(e.Message);

                        throw new CloudWatchAppenderException("CloudWatchAppender encountered an error while submitting to CloudWatch.", e);
                    }
                });

            if (!task.IsCompleted)
                _tasks.TryAdd(task.Id, task);

            task.ContinueWith(t => _tasks.TryRemove(task.Id, out task));
        }
    }

    public class EventCap
    {
        private int _max;

        public EventCap(int max)
        {
            _max = max;
        }

        public EventCap()
        {
        }

        public bool Request(DateTime timeStamp)
        {
            if (_max <= 0)
                return true;

            return true;
        }
    }

    internal class CloudWatchAppenderException : Exception
    {
        public CloudWatchAppenderException(string msg, Exception innerException)
            : base(msg, innerException)
        {

        }
    }
}