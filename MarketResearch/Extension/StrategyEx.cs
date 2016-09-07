using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Ats.Core;
using MarketResearch.Extension;

namespace MarketResearch.Extension
{
    public class StrategyEx : Strategy
    {
        [Parameter(Display="更新交易时间片段", Description = "是否实时更新交易时间片", Category = "更新")]
        public bool EnableUpdateSlice;

        [Parameter(Display = "交易时间片容忍度(秒)", Description = "当前交易时间处于哪个交易时间片的容忍度", Category = "系统")]
        public double TimeSliceTolerance = 120; // 2 min

        [Parameter(Display = "日盘交易开始时间(24小时)", Description = "日盘交易的开始时间，一般与交易所的时间前移1小时", Category = "系统")]
        public int DayTradeBegin = 8; // 9 - 1

        [Parameter(Display = "日盘交易结束时间(24小时)", Description = "日盘交易结束的时间， 一般与交易所的时间后移1小时", Category = "系统")]
        public int DayTradeEnd = 16; // 15 + 1

        protected bool _isTradeBreak = false; // 是否处于交易休息时间
        protected bool _hasNightTrade = false; //是否有夜盘
        protected TradeDate _tradeDate = TradeDate.OnDay;

        protected DateTime _tOpen;
        protected DateTime _tClose;
        protected TimeSliceEx _currentTradeSlice; //当前交易时间片
        protected List<TimeSliceEx> _tradeSlices; //驱动品种的所有交易时间片
        protected Future _triggerFuture = null; //驱动品种
        protected Exchange _triggerExchange = null; //驱动品种所在的交易所

        public DateTime CurrentTime { get { return _triggerExchange.TimeNow; } }
        public TimeSliceEx CurrentSlice { get { return _currentTradeSlice; } }
        public TimeSliceEx LastTradeSlice { get { return _tradeSlices[_tradeSlices.Count - 1]; } }

        #region Init
        public virtual bool CustomInit()
        {
            if (!initTriggerFuture() || 
                !initTriggerExchange() || 
                !initTradeSlice()) return false;

            return true;
        }

        protected bool initTriggerFuture()
        {
            if (AllFutures == null || AllFutures.Count < 1) return false;

            foreach (Future f in AllFutures)
            {
                if (f.ID.Equals(TriggerInstrument))
                {
                    _triggerFuture = f;
                    return true;
                }
            }

            return false;
        }

        protected bool initTriggerExchange()
        {
            if (_triggerFuture == null) return false;

            _triggerExchange = GetExchange(_triggerFuture.ExchangeID);

            return true;
        }

        protected bool initTradeSlice()
        {
            _tradeSlices = SortInstrumentTradingTime(TriggerInstrument);
            if (_tradeSlices == null || _tradeSlices.Count == 0)
            {
                Print("Trade slices NOT found:" + TriggerInstrument);
                return false;
            }

            return true;
        }
        #endregion

        #region Update
        public void Update()
        {
            updateTradeSlice();
            updateTradeDay();
        }

        protected void updateTradeSlice()
        {
            _currentTradeSlice = null;

            double currentExchangeTime = CurrentTime.TimeOfDay.TotalSeconds + TimeSliceTolerance;
            foreach (TimeSliceEx slice in _tradeSlices)
            {
                if (currentExchangeTime > slice.Slice.BeginTime.TotalSeconds &&
                    currentExchangeTime < slice.Slice.EndTime.TotalSeconds)
                {
                    _currentTradeSlice = slice;
                }
            }

            _isTradeBreak = _currentTradeSlice == null;
        }

        protected void updateTradeDay()
        {
            if (_currentTradeSlice != null)
            {
                _tradeDate = _currentTradeSlice.IsDayTrade ? TradeDate.OnDay : TradeDate.OnNight;
            }else //说明处于交易所休息时间段，例如早上10：20，或者当天交易还没开始，或者交易已经结束
            {
                int hours = CurrentTime.TimeOfDay.Hours;
                if (hours > DayTradeBegin && hours < DayTradeEnd) _tradeDate = TradeDate.OnDay;
                else _tradeDate = TradeDate.OnNight;
            }
        }
        #endregion

        #region Debug & Msg
        public void PrintRunningDate()
        {
            Print("当前系统时间： " + Now.ToString());
            Print("交易所时间" + CurrentTime);
        }
        #endregion

        #region Misc
        /* 对交易时间进行排序，结果为：夜盘->白盘
         * 21:0:0 - 23:0:0
         * 24:0:0 - 2:0:0
         * 09:0:0 - 10:15:0
         * 10:30:0 - 11:30:0
         * 13:30:0 - 15:00:0
         */
        public List<TimeSliceEx> SortInstrumentTradingTime(string instrumentID)
        {
            List<TimeSlice> slices = GetInstrumentTradingTime(instrumentID);
            List<TimeSliceEx> tse = TimeSliceEx.CreateSlices(slices, DayTradeBegin, DayTradeEnd);
            tse.Sort(new TimeSliceComparer());
            return tse;
        }
        #endregion

        //复盘时，策略一次性运行完夜盘和白盘，因此每个交易日，该函数只会被调用一次。
        protected void onSimulateTimesSetup(List<TimeSlice> slices)
        {
            //检测是否有夜盘
            TimeSlice nightChecker = new TimeSlice(new TimeSpan(20, 0, 0), new TimeSpan(23, 0, 0));
            foreach (var slice in slices)
            {
                if (slice.BeginTime > nightChecker.BeginTime &&
                    slice.BeginTime < nightChecker.EndTime)
                {
                    _hasNightTrade = true;
                    UtilsEx.MakeDateByNight(TradingDate, slice);
                    break;
                }
            }

            //设置白天
            TimeSlice dayCloseChecker = new TimeSlice(new TimeSpan(14, 50, 0), new TimeSpan(16, 0, 0));
            foreach (var slice in slices)
            {
                if (slice.EndTime > dayCloseChecker.BeginTime &&
                    slice.EndTime < dayCloseChecker.EndTime)
                {
                    UtilsEx.MakeDateByNight(TradingDate, slice);
                }
            }

            //设置没有夜盘的期货品种的开盘时间。
            if (!_hasNightTrade)
            {
                TimeSlice dayOpenChecker = new TimeSlice(new TimeSpan(8, 50, 0), new TimeSpan(10, 0, 0));
                foreach (var slice in slices)
                {
                    if (slice.BeginTime > dayOpenChecker.BeginTime &&
                        slice.BeginTime < dayOpenChecker.EndTime)
                    {
                        _tOpen = UtilsEx.MakeDateByDay(TradingDate, slice.BeginTime);
                    }
                }
            }
        }

        public enum TradeDate
        {
            OnNight = 0,
            OnDay
        }
    }
}
