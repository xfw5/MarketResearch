using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Ats.Core;
using MarketResearch.Extension;
using MarketResearch.Helper;

namespace MarketResearch.Extension
{
    /*
     * 扩展MQ自带的策略类，以后所有自定义策略的开发均基于该扩展类来开发。
     */
    public abstract class StrategyEx : Strategy
    {
        #region Param
        [Parameter(Display = "打印仓位状态当成交回报时", Description = "是否打印仓位状态，当上报的交易成功回报时", Category = "调试")]
        public bool PrintPositionStatusOnTradeDeal = false;

        [Parameter(Display="更新交易时间片段", Description = "是否实时更新交易时间片", Category = "更新")]
        public bool EnableUpdateSlice;

        [Parameter(Display = "更新策略状态方式", Description = "方式： 定时器，在Tick中，在Bar中", Category = "系统")]
        public E_UpdateType UpdateStatusType = E_UpdateType.InBar;

        [Parameter(Display = "更新策略的定时周期(秒)", Description = "该参数只有选中【更新策略状态方式】为【AsTimer】时才起作用", Category = "系统")]
        public int UpdateTimerDuration = 10;

        [Parameter(Display = "交易时间片容忍度(秒)", Description = "当前交易时间处于哪个交易时间片的容忍度", Category = "系统")]
        public double TimeSliceTolerance = 120; // 2 min

        [Parameter(Display = "日盘交易开始时间(24小时)", Description = "日盘交易的开始时间，一般与交易所的时间前移1小时", Category = "系统")]
        public int DayTradeBegin = 8; // 9 - 1

        [Parameter(Display = "日盘交易结束时间(24小时)", Description = "日盘交易结束的时间， 一般与交易所的时间后移1小时", Category = "系统")]
        public int DayTradeEnd = 16; // 15 + 1

        [Parameter(Display = "买卖价差", Description = "买入或卖出时，与当前市价的价差。单位为最小交易单元", Category = "交易")]
        public int OrderPriceDiff = 5;
        #endregion

        #region Field
        protected bool _isTradeBreak = false; // 是否处于交易休息时间
        protected bool _hasNightTrade = false; //是否有夜盘
        protected E_TradeType _tradeType = E_TradeType.OnDay;

        protected DateTime _tOpen;
        protected DateTime _tClose;
        protected TimeSliceEx _currentTradeSlice; //当前交易时间片
        protected List<TimeSliceEx> _tradeSlices; //驱动品种的所有交易时间片
        protected Future _triggerFuture = null; //驱动品种
        protected Exchange _triggerExchange = null; //驱动品种所在的交易所

        public delegate void onOrderStatusChangeDelegate(Order order);
        protected onOrderStatusChangeDelegate _onOrderStatusChange; 
        #endregion

        #region property
        public bool IsTradeBreak { get { return _isTradeBreak; } }
        public bool HasNightTrade { get { return _hasNightTrade; } }
        public E_TradeType TradeType { get { return _tradeType; } }
        public DateTime CurrentTime { get { return _triggerExchange.TimeNow; } }
        public TimeSliceEx CurrentSlice { get { return _currentTradeSlice; } }
        public TimeSliceEx LastTradeSlice { get { return _tradeSlices[_tradeSlices.Count - 1]; } }
        public Future TriggerFuture { get { return _triggerFuture; } }
        #endregion

        #region Init
        public override void Init()
        {
            coreInit();

            onCustomInit(); // 策略自定义初始化入口

            onInitDone();
        }

        public abstract void onCustomInit(); // 抽象函数，每个自定义的策略都必须实现该函数，来完成策略的自定义初始化

        // 策略内部初始化
        private bool coreInit()
        {
            if (!initTriggerFuture() || 
                !initTriggerExchange() || 
                !initTradeSlice()) return false;

            initTradeType();
            return true;
        }

        // 策略初始化完成，准备就绪
        protected virtual void onInitDone()
        {
            if (UpdateStatusType == E_UpdateType.AsTimer)
            {
                StartTimer(UpdateTimerDuration * 1000, Update, "StrategyExStatusUpdateTimer");
            }
        }

        // 初始化驱动品种
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

        // 获取驱动品种所在交易所
        protected bool initTriggerExchange()
        {
            if (_triggerFuture == null) return false;

            _triggerExchange = GetExchange(_triggerFuture.ExchangeID);

            return true;
        }

        // 获取驱动品种的交易时间片， 并按照夜盘到日盘排序
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

        protected void initTradeType()
        {
            if (_tradeSlices == null) initTradeSlice();

            if (_tradeSlices == null) return;

            foreach (TimeSliceEx se in _tradeSlices)
            {
                if (!se.IsDayTrade)
                {
                    _hasNightTrade = true;
                    break;
                }
            }
        }
        #endregion

        #region Update
        // 更新策略状态
        public void Update(object state)
        {
            StrategyExHelper.PrintRunningDate(this);

            updateTradeSlice();
            updateTradeDay();

            StrategyExHelper.PrintRuntimeStatus(this);
        }

        // 更新当前时间，判断处于哪个交易时间片
        protected void updateTradeSlice()
        {
            _currentTradeSlice = null;

            double currentExchangeTime = CurrentTime.TimeOfDay.TotalSeconds;
            //凌晨时间
            if (CurrentTime.TimeOfDay.Hours < DayTradeBegin) currentExchangeTime += UtilsEx.OndayInSeconds;

            double secondsBegin = 0;
            double secondsEnd = 0;
            foreach (TimeSliceEx slice in _tradeSlices)
            {
                secondsBegin = slice.Slice.BeginTime.TotalSeconds - TimeSliceTolerance;
                secondsEnd = slice.Slice.EndTime.TotalSeconds + TimeSliceTolerance;
                if (!slice.IsDayTrade)
                {
                    // 夜盘，且跨日期(例如：21:00:00 - 02:00:00)
                    if (slice.Slice.BeginTime.Hours < DayTradeBegin) secondsBegin += UtilsEx.OndayInSeconds;
                    if (slice.Slice.EndTime.Hours < DayTradeBegin) secondsEnd += UtilsEx.OndayInSeconds;
                }

                if (currentExchangeTime > secondsBegin &&
                    currentExchangeTime < secondsEnd)
                {
                    _currentTradeSlice = slice;
                    break;
                }
            }

            // 是否处于交易休息时间
            _isTradeBreak = _currentTradeSlice == null;
        }

        // 更新当前的盘类型，是夜盘还是白盘
        protected void updateTradeDay()
        {
            if (_currentTradeSlice != null)
            {
                _tradeType = _currentTradeSlice.IsDayTrade ? E_TradeType.OnDay : E_TradeType.OnNight;
            }else //说明处于交易所休息时间段，例如早上10：20，或者当天交易还没开始，或者交易已经结束
            {
                int hours = CurrentTime.TimeOfDay.Hours;
                if (hours > DayTradeBegin && hours < DayTradeEnd) _tradeType = E_TradeType.OnDay;
                else _tradeType = E_TradeType.OnNight;
            }
        }
        #endregion

        #region Order Status
        public override void OnOrder(Order order)
        {
            if (_onOrderStatusChange != null) _onOrderStatusChange(order);
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

        public double GetSelloutPrice(double lastPrice, double priceTick)
        {
            return lastPrice - priceTick * OrderPriceDiff;
        }

        public double GetBuyInPrice(double lastPrice, double priceTick)
        {
            return lastPrice + priceTick * OrderPriceDiff;
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

        // 盘类型
        public enum E_TradeType
        {
            OnNight = 0, // 夜盘
            OnDay // 日盘
        }

        // 更新系统状态的方式
        public enum E_UpdateType
        {
            AsTimer = 0, // 以定时器的方式
            InTick, // 在tick事件中
            InBar // 在bar（k线）事件中
        }
    }
}
