using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Ats.Core;
using MarketResearch.Extension;
using MarketResearch.Helper;

namespace MarketResearch
{
    /*
     * 日内跨商品对冲策略，基于黄金，白银1602开发：
     * 1. 支持夜盘
     */
    public class WatchAndBreakout: StrategyEx
    {
        #region Param
        [Parameter(Display = "打印涨幅差", Description = "是否打印A、B的涨幅差", Category = "调试")]
        public bool DebugChangePercentage = false;

        [Parameter(Display = "观察时间(H)", Description="开盘后观察市场变化的时间长度，单位小时", Category= "期间")]
        public float DurationWatchChangeInHours = 0.5f;

        [Parameter(Display = "平仓终止策略时间(H)", Description = "当满足平仓时，终止该策略的运行如果距离收盘时间小于该值， 单位小时", Category = "期间")]
        public float DurationTerminalRunningIfPositionEmptyInHours = 3.0f;

        [Parameter(Display = "无条件平仓时间(M)", Description = "当距离收盘时间小于该值时，无条件平仓， 单位分钟", Category = "期间")]
        public float DurationUnconditionLiquidateInMinutes = 3.0f;

        [Parameter(Display = "尝试平仓时间(H)", Description = "当距离收盘时间小于该值时，如果能够覆盖成本，尝试平仓", Category = "期间")]
        public float DurationTryLiquidateInHours = 0.5f;

        [Parameter(Display = "标准涨幅差SCP", Description = "2支监视的期货品种之间的涨幅差值", Category = "指标")]
        public float StandardChangePercentage = 0.2f;

        [Parameter(Display = "附加涨幅差Extra", Description = "衡量2支品种涨幅差D时，附加的对比差值：D 与 SCP + Extra大小关系", Category = "指标")]
        public float ExtraChangePercentage = 0.4f;

        [Parameter(Display = "平仓涨幅差", Description = "2支品种之间的涨幅差值小于该值时，平仓", Category = "指标")]
        public float LiquidateChangePercentage = 0.2f;

        [Parameter(Display = "标准涨幅差筛选阀值", Description = "如果2支品种的涨幅差D大于该值，则使用D做为涨幅差衡量的标准，否则使用StandardChangePercentage来衡量", Category = "阀值")]
        public float SelectorChangePercentage = 0.3f;

        [Parameter(Display = "每手A", Description = "A品种每次交易的手数", Category = "交易")]
        public int HandsOfA = 1;

        [Parameter(Display = "每手B", Description = "B品种每次交易的手数", Category = "交易")]
        public int HandsOfB = 5;

        [Parameter(Display = "杠杆A", Description = "A品种的杠杆", Category = "交易")]
        public int LeverageA = 1;

        [Parameter(Display = "杠杆B", Description = "B品种的杠杆", Category = "交易")]
        public int LeverageB = 1;
        #endregion

        #region Field
        private string _instrumentA; // 对应第一个期货品种
        private string _instrumentB; // 对应第二个期货品种

        #region Cache
        private Future _futureA;
        private Future _futureB;
        private Order _orderA;
        private Order _orderB;
        private Trade _tradeA;
        private Trade _tradeB;
        private Tick _tickA;
        private Tick _tickB;
        #endregion

        // 状态机
        private ERunningState _runningState = ERunningState.InitFailed;

        // 定义状态机的处理函数
        private static StgStateMechine[] _stateHandler = new[] 
        {
            new StgStateMechine(ERunningState.InitFailed, onActionInitFailed),
            new StgStateMechine(ERunningState.DurationWatch, onActionWatchDuration), 
            new StgStateMechine(ERunningState.RealTimeExchange, onActionRealTimeEchange),
            new StgStateMechine(ERunningState.TryLiquidate, onTryLiquidate), 
            new StgStateMechine(ERunningState.UnconditionLiquidate, onUnconditionLiquidate), 
            new StgStateMechine(ERunningState.Stop, onActionStopRunning),
        };

        // 截止期限
        private DateTime _deadlineWatching;
        private DateTime _deadlineStopRunning;
        private DateTime _deadlineUnconditionLiquidate;
        private DateTime _deadlineTryLiquidate;

        // 计数器
        private long _countOfChangeInWatchingDuration;
        private double _totalChangePercentage; // = 驱动品种 - 另外一个
        private float _avgChangePercentage;

        // 阀值
        private double _thresholdChangePercentage;

        // 仓位状态
        private bool _isPositionEmpty = true;
        private bool _hasStop = false;
        #endregion

        #region Init & Setup
        public override void onCustomInit()
        {
            if (AllFutures.Count != 2)
            {
                _runningState = ERunningState.InitFailed;
            }

            _futureA = AllFutures[0];
            _futureB = AllFutures[1];
            _instrumentA = _futureA.ID;
            _instrumentB = _futureB.ID;

            StrategyExHelper.PrintRunningDate(this);

            setupDeadline();

            _runningState = ERunningState.DurationWatch;
        }

        // 根据配置的参数来设置策略截止期限
        private void setupDeadline()
        {
            DateTime preDay = TradingDayHelper.GetPreTradingDay(TradingDate);
            _deadlineWatching = UtilsEx.CalcDeadline(TradingDate, preDay, _tradeSlices, UtilsEx.DeadlineDir.ByBegin, DurationWatchChangeInHours * 60);
            _deadlineStopRunning = UtilsEx.CalcDeadline(TradingDate, preDay, _tradeSlices, UtilsEx.DeadlineDir.ByEnd, DurationTerminalRunningIfPositionEmptyInHours * 60);
            _deadlineUnconditionLiquidate = UtilsEx.CalcDeadline(TradingDate, preDay, _tradeSlices, UtilsEx.DeadlineDir.ByEnd, DurationUnconditionLiquidateInMinutes);
            _deadlineTryLiquidate = UtilsEx.CalcDeadline(TradingDate, preDay, _tradeSlices, UtilsEx.DeadlineDir.ByEnd, DurationTryLiquidateInHours * 60);

            Print("截止时间->观察: " + _deadlineWatching);
            Print("截止时间->平仓停止策略运行: " + _deadlineStopRunning);
            Print("截止时间->无条件平仓: " + _deadlineUnconditionLiquidate);
            Print("截止时间->尝试平仓: " + _deadlineTryLiquidate);
        }
        #endregion

        #region Build-in event
        public override void OnTick(Tick tick)
        {
            if (UpdateStatusType == E_UpdateType.InTick) Update(this);

            // 如果当天停牌，没有tick数据
            foreach (Future f in AllFutures)
            {
                if (f == null) return;
            }

            _stateHandler[(int)_runningState].Handler(this, tick);
        }

        public override void OnBar(Bar bar)
        {
            if (UpdateStatusType == E_UpdateType.InBar) Update(this);

            _stateHandler[(int)_runningState].Handler(this, bar);
        }

        public override void OnTrade(Trade trade)
        {
            Print("===========成交回报===========");
            OrderHelper.PrintOnTradeStatus(this, trade);

            if (trade.InstrumentID.Equals(_futureA.ID)) _tradeA = trade;
            else if (trade.InstrumentID.Equals(_futureB.ID)) _tradeB = trade;

            if (_tradeA != null && _tradeA.OpenOrClose == EnumOpenClose.平仓 && 
                _tradeB != null && _tradeB.OpenOrClose == EnumOpenClose.平仓)
            {
                _isPositionEmpty = true;
                _tradeA = _tradeB = null;
            }
        }

        public override void OnOrderRejected(Order order)
        {
            Print("==========订单被驳回==========");
            OrderHelper.PrintOrderStatus(this, order);
        }

        public override void OnOrderCanceled(Order order)
        {
            Print("==========订单撤回成功==========");
            OrderHelper.PrintOrderStatus(this, order);
        }

        public override void OnCancelOrderFailed(string orderGuid, string msg)
        {
            Print("==========订单撤回失败==========");
            Print("GUID:" + orderGuid);
            Print("原因：" + msg);
        }
        #endregion

        #region Action
        private void calcWatchingResult()
        {
            _avgChangePercentage = (float)(_totalChangePercentage / _countOfChangeInWatchingDuration);

            float deltaChangeWithStandard = Math.Abs(Math.Abs(_avgChangePercentage) - StandardChangePercentage);

            if (deltaChangeWithStandard > SelectorChangePercentage) _thresholdChangePercentage = _avgChangePercentage;
            else _thresholdChangePercentage = StandardChangePercentage;

            _thresholdChangePercentage += ExtraChangePercentage;
            printWatchingResult();
        }
        #endregion

        #region Misc
        private double getChangePercentage(bool isTick)
        {
            if (isTick)
            {
                if (_tickA == null) _tickA = LastFutureTick(_instrumentA);
                if (_tickB == null) _tickB = LastFutureTick(_instrumentB);

                if (_tickA == null || _tickB == null) return 0.0;

                double delta = StrategyExHelper.Change(_tickA) - StrategyExHelper.Change(_tickB);
                if (DebugChangePercentage) Print("涨幅差(DA-DB): " + delta);
                return delta;
            }

            Print("不支持K先驱动方式");
            return 0;
        }

        private bool isDeadlineHit(DateTime measureDeadline)
        {
            return CurrentTime >= measureDeadline;
        }

        private void printWatchingResult()
        {
            Print("=========>观察期间结束，观察结果:");
            Print("         平均涨幅差： " + _avgChangePercentage);
            Print("         涨幅差衡量标准： " + StandardChangePercentage);
            Print("         涨幅差选择阀值： " + SelectorChangePercentage);
            Print("         选中衡量结果： " + _thresholdChangePercentage + "(其中附加衡量涨幅差:" + ExtraChangePercentage + ")");

            Print("         观察期间统计次数: " + _countOfChangeInWatchingDuration);
        }
        #endregion

        #region Order
        private Order SendOrderEx(Future future, double priceLimite, int volume, EnumBuySell buySell, EnumOpenClose openClose)
        {
            return SendOrder(DefaultAccount, future.ID, future.Market, future.ExchangeID, priceLimite,
                volume, buySell, openClose, 
                EnumOrderPriceType.市价, EnumOrderTimeForce.当日有效, EnumHedgeFlag.投机);
        }

        private void onOpenPosition(double deltaChange)
        {
            if (deltaChange < 0) // DA < DB
            {
                _orderA = SendOrderEx(_futureA, _tickA.BidPrice1, HandsOfA * LeverageA, EnumBuySell.买入, EnumOpenClose.开仓); // 空
                _orderB = SendOrderEx(_futureB, _tickB.AskPrice1, HandsOfB * LeverageB, EnumBuySell.卖出, EnumOpenClose.开仓); // 多
            }
            else
            {
                _orderA = SendOrderEx(_futureA, _tickA.BidPrice1, HandsOfA * LeverageA, EnumBuySell.卖出, EnumOpenClose.开仓); // 多
                _orderB = SendOrderEx(_futureB, _tickB.AskPrice1, HandsOfB * LeverageB, EnumBuySell.买入, EnumOpenClose.开仓); // 空
            }

            _isPositionEmpty = false;
        }

        private void onLiquidatePosition()
        {
            if (_orderA != null)
            {
                if (_orderA.Direction == EnumBuySell.买入) SendOrderEx(_futureA, _tickA.BidPrice1, HandsOfA * LeverageA, EnumBuySell.卖出, EnumOpenClose.平仓);
                else if (_orderA.Direction == EnumBuySell.卖出) SendOrderEx(_futureA, _tickA.AskPrice1, HandsOfA * LeverageA, EnumBuySell.买入, EnumOpenClose.平仓);
            }

            if (_orderB != null)
            {
                if (_orderB.Direction == EnumBuySell.买入) SendOrderEx(_futureB, _tickB.BidPrice1, HandsOfB * LeverageB, EnumBuySell.卖出, EnumOpenClose.平仓);
                else if (_orderB.Direction == EnumBuySell.卖出) SendOrderEx(_futureB, _tickB.AskPrice1, HandsOfB * LeverageB, EnumBuySell.买入, EnumOpenClose.平仓);
            }
        }
        #endregion

        #region Mechine state handler
        private static void onActionInitFailed(WatchAndBreakout st, object customData)
        {
            st.Print("对冲必须设置2支期货品种!");
        }

        private static void onActionWatchDuration(WatchAndBreakout st, object customData)
        {
            // 如果观察结束，计算观察期间的涨幅差，切换状态机，进入实时交易阶段。
            if (st.isDeadlineHit(st._deadlineWatching))
            {
                st.calcWatchingResult();

                st._runningState = ERunningState.RealTimeExchange;
                return;
            }

            // 如果是tick驱动，算计该次的涨幅差，增加触发计时器。
            Tick tick = customData as Tick;
            if (tick != null)
            {
                st._countOfChangeInWatchingDuration++;
                st._totalChangePercentage += Math.Abs(st.getChangePercentage(true));
                return;
            }
        }

        private static void onActionRealTimeEchange(WatchAndBreakout st, object customData)
        {
            Tick tick = customData as Tick;
            if (tick != null)
            {
                st._tickA = st.LastFutureTick(st._instrumentA);
                st._tickB = st.LastFutureTick(st._instrumentB);
                double deltaChange = st.getChangePercentage(true);
                double deltaChangeAbs = Math.Abs(deltaChange);

                if (st._isPositionEmpty)
                {
                    if (deltaChangeAbs > st._thresholdChangePercentage)
                    {
                        st.onOpenPosition(deltaChange);
                    }
                }else
                {
                    if (deltaChangeAbs < st.LiquidateChangePercentage)
                    {
                        st.onLiquidatePosition();
                    }
                }
            }
        }

        private static void onTryLiquidate(WatchAndBreakout st, object customData)
        {

        }

        private static void onUnconditionLiquidate(WatchAndBreakout st, object customData)
        {

        }

        private static void onActionStopRunning(WatchAndBreakout st, object customData)
        {
            if (st._hasStop) return;

            st.Print("策略停止运行!");
            st.Exit();
        }
        #endregion

        #region Mechine state defines
        // 状态
        public enum ERunningState
        {
            InitFailed = 0, // 策略初始化失败
            DurationWatch, // 策略观察期间
            RealTimeExchange, // 观察结束，开始实时交易
            TryLiquidate,
            UnconditionLiquidate,
            Stop, // 策略停止运行
        }

        // 自定义的策略状态机
        public class StgStateMechine
        {
            public delegate void OnStateHandlerFunc(WatchAndBreakout stg, object customData);

            public ERunningState State;
            public OnStateHandlerFunc Handler; //策略处理入口函数.

            public StgStateMechine(ERunningState state, OnStateHandlerFunc handler)
            {
                this.State = state;
                this.Handler = handler;
            }
        }
        #endregion
    }
}
