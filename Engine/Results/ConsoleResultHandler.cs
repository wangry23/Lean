﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); 
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/

/**********************************************************
* USING NAMESPACES
**********************************************************/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Packets;

namespace QuantConnect.Lean.Engine.Results
{
    /// <summary>
    /// Console local resulthandler passes messages back to the console/local GUI display.
    /// </summary>
    public class ConsoleResultHandler : IResultHandler
    {
        /******************************************************** 
        * PRIVATE VARIABLES
        *********************************************************/
        private bool _exitTriggered = false;
        private IConsoleStatusHandler _algorithmNode;
        private DateTime _updateTime = new DateTime();
        private DateTime _lastSampledTimed;
        private IAlgorithm _algorithm;
        private int _jobDays = 0;
        private bool _isActive = true;
        private object _chartLock = new Object();

        //Sampling Periods:
        private TimeSpan _resamplePeriod = TimeSpan.FromMinutes(4);
        private TimeSpan _notificationPeriod = TimeSpan.FromSeconds(2);

        /******************************************************** 
        * PUBLIC PROPERTIES
        *********************************************************/
        /// <summary>
        /// Messaging to store notification messages for processing.
        /// </summary>
        public ConcurrentQueue<Packet> Messages 
        {
            get;
            set;
        }

        /// <summary>
        /// Local object access to the algorithm for the underlying Debug and Error messaging.
        /// </summary>
        public IAlgorithm Algorithm
        {
            get
            {
                return _algorithm;
            }
            set
            {
                _algorithm = value;
            }
        }

        /// <summary>
        /// Charts collection for storing the master copy of user charting data.
        /// </summary>
        public ConcurrentDictionary<string, Chart> Charts 
        {
            get;
            set;
        }

        /// <summary>
        /// Boolean flag indicating the result hander thread is busy. 
        /// False means it has completely finished and ready to dispose.
        /// </summary>
        public bool IsActive {
            get
            {
                return _isActive;
            }
        }

        /// <summary>
        /// Sampling period for timespans between resamples of the charting equity.
        /// </summary>
        /// <remarks>Specifically critical for backtesting since with such long timeframes the sampled data can get extreme.</remarks>
        public TimeSpan ResamplePeriod
        {
            get
            {
                return _resamplePeriod;
            }
        }

        /// <summary>
        /// How frequently the backtests push messages to the browser.
        /// </summary>
        /// <remarks>Update frequency of notification packets</remarks>
        public TimeSpan NotificationPeriod
        {
            get
            {
                return _notificationPeriod;
            }
        }

        /******************************************************** 
        * PUBLIC CONSTRUCTOR
        *********************************************************/
        /// <summary>
        /// Console result handler constructor.
        /// </summary>
        /// <remarks>Setup the default sampling and notification periods based on the backtest length.</remarks>
        public ConsoleResultHandler(AlgorithmNodePacket packet) 
        {
            Log.Trace("Launching Console Result Handler: QuantConnect v2.0");
            Messages = new ConcurrentQueue<Packet>();
            Charts = new ConcurrentDictionary<string, Chart>();
            _chartLock = new Object();
            _isActive = true;

            // we expect one of two types here, the backtest node packet or the live node packet
            if (packet is BacktestNodePacket)
            {
                var backtest = packet as BacktestNodePacket;
                _algorithmNode = new BacktestConsoleStatusHandler(backtest);
            }
            else
            {
                var live = packet as LiveNodePacket;
                if (live == null)
                {
                    throw new ArgumentException("Unexpected AlgorithmNodeType: " + packet.GetType().Name);
                }
                _algorithmNode = new LiveConsoleStatusHandler(live);
            }

            _resamplePeriod = _algorithmNode.ComputeSampleEquityPeriod();

            //Notification Period for pushes:
            _notificationPeriod = TimeSpan.FromSeconds(5);
        }

        /******************************************************** 
        * PUBLIC METHODS
        *********************************************************/
        /// <summary>
        /// Entry point for console result handler thread.
        /// </summary>
        public void Run()
        {
            while ( !_exitTriggered || Messages.Count > 0 ) 
            {
                while (Messages.Count > 0)
                {
                    Packet packet;
                    if (!Messages.TryDequeue(out packet)) continue;

                    switch (packet.Type)
                    { 
                        case PacketType.Log:
                            var log = packet as LogPacket;
                            Log.Trace("Log Message >> " + log.Message);
                            break;
                        case PacketType.Debug:
                            var debug = packet as DebugPacket;
                            Log.Trace("Debug Message >> " + debug.Message);
                            break;
                    }
                }
                Thread.Sleep(100);

                if (DateTime.Now > _updateTime)
                {
                    _updateTime = DateTime.Now.AddSeconds(5);
                    _algorithmNode.LogAlgorithmStatus(_lastSampledTimed);
                }
            }

            Log.Trace("ConsoleResultHandler: Ending Thread...");
            _isActive = false;
        }

        /// <summary>
        /// Send a debug message back to the browser console.
        /// </summary>
        /// <param name="message">Message we'd like shown in console.</param>
        public void DebugMessage(string message)
        {
            //Don't queue up identical messages:
            Messages.Enqueue(new DebugPacket(0, "", "", message));
        }

        /// <summary>
        /// Send a logging message to the log list for storage.
        /// </summary>
        /// <param name="message">Message we'd in the log.</param>
        public void LogMessage(string message)
        {
            Messages.Enqueue(new LogPacket("", message));
        }

        /// <summary>
        /// Send a runtime error message back to the browser highlighted with in red 
        /// </summary>
        /// <param name="message">Error message.</param>
        /// <param name="stacktrace">Stacktrace information string</param>
        public void RuntimeError(string message, string stacktrace = "")
        {
            Messages.Enqueue(new RuntimeErrorPacket("", message, stacktrace));
        }

        /// <summary>
        /// Send an error message back to the console highlighted in red with a stacktrace.
        /// </summary>
        /// <param name="message">Error message we'd like shown in console.</param>
        /// <param name="stacktrace">Stacktrace information string</param>
        public void ErrorMessage(string message, string stacktrace = "")
        {
            Messages.Enqueue(new RuntimeErrorPacket("", message, stacktrace));
        }

        /// <summary>
        /// Add a sample to the chart specified by the chartName, and seriesName.
        /// </summary>
        /// <param name="chartName">String chart name to place the sample.</param>
        /// <param name="chartType">Type of chart we should create if it doesn't already exist.</param>
        /// <param name="seriesName">Series name for the chart.</param>
        /// <param name="seriesType">Series type for the chart.</param>
        /// <param name="time">Time for the sample</param>
        /// <param name="value">Value for the chart sample.</param>
        /// <remarks>Sample can be used to create new charts or sample equity - daily performance.</remarks>
        public void Sample(string chartName, ChartType chartType, string seriesName, SeriesType seriesType, DateTime time, decimal value)
        {
            lock (_chartLock)
            {
                //Add a copy locally:
                if (!Charts.ContainsKey(chartName))
                {
                    Charts.AddOrUpdate<string, Chart>(chartName, new Chart(chartName, chartType));
                }

                //Add the sample to our chart:
                if (!Charts[chartName].Series.ContainsKey(seriesName))
                {
                    Charts[chartName].Series.Add(seriesName, new Series(seriesName, seriesType));
                }

                //Add our value:
                Charts[chartName].Series[seriesName].Values.Add(new ChartPoint(time, value));
            }
        }

        /// <summary>
        /// Sample the strategy equity at this moment in time.
        /// </summary>
        /// <param name="time">Current time</param>
        /// <param name="value">Current equity value</param>
        public void SampleEquity(DateTime time, decimal value)
        {
            Sample("Strategy Equity", ChartType.Stacked, "Equity", SeriesType.Candle, time, value);
            _lastSampledTimed = time;
        }

        /// <summary>
        /// Sample today's algorithm daily performance value.
        /// </summary>
        /// <param name="time">Current time.</param>
        /// <param name="value">Value of the daily performance.</param>
        public void SamplePerformance(DateTime time, decimal value)
        {
            Sample("Strategy Equity", ChartType.Overlay, "Daily Performance", SeriesType.Line, time, value);
        }


        /// <summary>
        /// Analyse the algorithm and determine its security types.
        /// </summary>
        /// <param name="types">List of security types in the algorithm</param>
        public void SecurityType(List<SecurityType> types)
        {
            //NOP
        }

        /// <summary>
        /// Send an algorithm status update to the browser.
        /// </summary>
        /// <param name="algorithmId">Algorithm id for the status update.</param>
        /// <param name="status">Status enum value.</param>
        /// <param name="message">Additional optional status message.</param>
        /// <remarks>In backtesting we do not send the algorithm status updates.</remarks>
        public void SendStatusUpdate(string algorithmId, AlgorithmStatus status, string message = "")
        {
            Log.Trace("ConsoleResultHandler.SendStatusUpdate(): Algorithm Status: " + status + " : " + message);
        }


        /// <summary>
        /// Sample the asset prices to generate plots.
        /// </summary>
        /// <param name="symbol">Symbol we're sampling.</param>
        /// <param name="time">Time of sample</param>
        /// <param name="value">Value of the asset price</param>
        public void SampleAssetPrices(string symbol, DateTime time, decimal value)
        { 
            //NOP. Don't sample asset prices in console.
        }


        /// <summary>
        /// Add a range of samples to the store.
        /// </summary>
        /// <param name="updates">Charting updates since the last sample request.</param>
        public void SampleRange(List<Chart> updates)
        {
            lock (_chartLock)
            {
                foreach (var update in updates)
                {
                    //Create the chart if it doesn't exist already:
                    if (!Charts.ContainsKey(update.Name))
                    {
                        Charts.AddOrUpdate(update.Name, new Chart(update.Name, update.ChartType));
                    }

                    //Add these samples to this chart.
                    foreach (var series in update.Series.Values)
                    {
                        //If we don't already have this record, its the first packet
                        if (!Charts[update.Name].Series.ContainsKey(series.Name))
                        {
                            Charts[update.Name].Series.Add(series.Name, new Series(series.Name, series.SeriesType));
                        }

                        //We already have this record, so just the new samples to the end:
                        Charts[update.Name].Series[series.Name].Values.AddRange(series.Values);
                    }
                }
            }
        }

        
        /// <summary>
        /// Algorithm final analysis results dumped to the console.
        /// </summary>
        /// <param name="job">Lean AlgorithmJob task</param>
        /// <param name="orders">Collection of orders from the algorithm</param>
        /// <param name="profitLoss">Collection of time-profit values for the algorithm</param>
        /// <param name="holdings">Current holdings state for the algorithm</param>
        /// <param name="statistics">Statistics information for the algorithm (empty if not finished)</param>
        /// <param name="banner">Runtime statistics banner information</param>
        public void SendFinalResult(AlgorithmNodePacket job, Dictionary<int, Order> orders, Dictionary<DateTime, decimal> profitLoss, Dictionary<string, Holding> holdings, Dictionary<string, string> statistics, Dictionary<string, string> banner)
        {
            // Bleh. Nicely format statistical analysis on your algorithm results. Save to file etc.
            foreach (var pair in statistics) 
            {
                Log.Trace("STATISTICS:: " + pair.Key + " " + pair.Value);
            }
        }

        /// <summary>
        /// Set the Algorithm instance for ths result.
        /// </summary>
        /// <param name="algorithm">Algorithm we're working on.</param>
        /// <remarks>While setting the algorithm the backtest result handler.</remarks>
        public void SetAlgorithm(IAlgorithm algorithm) 
        {
            _algorithm = algorithm;
        }

        /// <summary>
        /// Terminate the result thread and apply any required exit proceedures.
        /// </summary>
        public void Exit()
        {
            _exitTriggered = true;
        }

        /// <summary>
        /// Send a new order event to the browser.
        /// </summary>
        /// <remarks>In backtesting the order events are not sent because it would generate a high load of messaging.</remarks>
        /// <param name="newEvent">New order event details</param>
        public void OrderEvent(OrderEvent newEvent)
        {
            Log.Trace("ConsoleResultHandler.OrderEvent(): id:" + newEvent.OrderId + " >> Status:" + newEvent.Status + " >> Fill Price: " + newEvent.FillPrice.ToString("C") + " >> Fill Quantity: " + newEvent.FillQuantity);
        }


        /// <summary>
        /// Set the current runtime statistics of the algorithm
        /// </summary>
        /// <param name="key">Runtime headline statistic name</param>
        /// <param name="value">Runtime headline statistic value</param>
        public void RuntimeStatistic(string key, string value)
        {
            Log.Trace("ConsoleResultHandler.RuntimeStatistic(): "  + key + " : " + value);
        }


        /// <summary>
        /// Clear the outstanding message queue to exit the thread.
        /// </summary>
        public void PurgeQueue() 
        {
            Messages.Clear();
        }

        /// <summary>
        /// Store result on desktop.
        /// </summary>
        /// <param name="packet">Packet of data to store.</param>
        /// <param name="async">Store the packet asyncronously to speed up the thread.</param>
        /// <remarks>Async creates crashes in Mono 3.10 if the thread disappears before the upload is complete so it is disabled for now.</remarks>
        public void StoreResult(Packet packet, bool async = false)
        {
            // Do nothing.
        }

        /// <summary>
        /// Provides an abstraction layer for live vs backtest packets to provide status/sampling to the AlgorithmManager
        /// </summary>
        /// <remarks>
        /// Since we can run both live and back test from the console, we need two implementations of what to do
        /// at certain times
        /// </remarks>
        private interface IConsoleStatusHandler
        {
            void LogAlgorithmStatus(DateTime current);
            TimeSpan ComputeSampleEquityPeriod();
        }

        // uses a const 2 second sample equity period and does nothing for logging algorithm status
        private class LiveConsoleStatusHandler : IConsoleStatusHandler
        {
            private readonly LiveNodePacket _job;
            public LiveConsoleStatusHandler(LiveNodePacket _job)
            {
                this._job = _job;
            }
            public void LogAlgorithmStatus(DateTime current)
            {
                // later we can log daily %Gain if possible
            }
            public TimeSpan ComputeSampleEquityPeriod()
            {
                return TimeSpan.FromSeconds(2);
            }
        }
        // computes sample equity period from 4000 samples evenly spaced over the backtest interval and logs %complete to log file
        private class BacktestConsoleStatusHandler : IConsoleStatusHandler
        {
            private readonly BacktestNodePacket _job;
            private readonly double _backtestSpanInDays;
            public BacktestConsoleStatusHandler(BacktestNodePacket _job)
            {
                this._job = _job;
                _backtestSpanInDays = (_job.PeriodFinish - _job.PeriodStart).TotalDays;
            }
            public void LogAlgorithmStatus(DateTime current)
            {
                var _daysProcessed = (current - _job.PeriodStart).TotalDays;
                Log.Trace("Progress: " + (_daysProcessed * 100 / _backtestSpanInDays).ToString("F2") + "% Processed: " + _daysProcessed + " days of total: " + (int)_backtestSpanInDays);
            }
            public TimeSpan ComputeSampleEquityPeriod()
            {
                const double samples = 4000;
                const double minimumSamplePeriod = 4 * 60;
                var totalMinutes = (_job.PeriodFinish - _job.PeriodStart).TotalMinutes;
                var resampleMinutes = (totalMinutes < (minimumSamplePeriod * samples)) ? minimumSamplePeriod : (totalMinutes / samples);
                return TimeSpan.FromMinutes(resampleMinutes);
            }
        }

    } // End Result Handler Thread:

} // End Namespace
