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
        private Trade _tradeA;
        private Trade _tradeB;
        private Tick _tickA;
        private Tick _tickB;
        private HedgeOrderTracer _orderStatusTracerA;
        private HedgeOrderTracer _orderStatusTracerB;
        #endregion

        // 状态机
        private ERunningState _runningState = ERunningState.Init;

        // 定义状态机的处理函数
        private static StgStateMechine[] _stateHandler = new[] 
        {
            new StgStateMechine(ERunningState.Init, onActionInit),
            new StgStateMechine(ERunningState.InitFailed, onActionInitFailed),
            new StgStateMechine(ERunningState.DurationWatch, onActionWatchDuration), 
            new StgStateMechine(ERunningState.RealTimeExchange, onActionRealTimeExchange),
            new StgStateMechine(ERunningState.TryLiquidate, onTryLiquidate), 
            new StgStateMechine(ERunningState.UnconditionLiquidate, onUnconditionLiquidate), 
            new StgStateMechine(ERunningState.Stop, onStopRunning),
        };

        // 截止期限
        private DateTime _deadlineWatching;
        private DateTime _deadlineStopRunningIfPositionEmpty;
        private DateTime _deadlineUnconditionLiquidate;
        private DateTime _deadlineTryLiquidate;

        // 定时器
        private DeadlineTimer _timerWatching;
        private DeadlineTimer _timerStopRunning;
        private DeadlineTimer _timerUnconditionLiquidate;
        private DeadlineTimer _timerTryLiquidate;

        // 计数器
        private long _countOfChangeInWatchingDuration;
        private double _totalChangePercentage; // = 驱动品种 - 另外一个
        private float _avgChangePercentage;

        // 阀值
        private double _thresholdChangePercentage;

        private bool _hasStop = false; // 策略是否已经停止运行
        #endregion

        #region Property
        // 仓位是否为空
        public bool IsPositionEmpty { get { return _orderStatusTracerA.IsPositionEmpty && _orderStatusTracerB.IsPositionEmpty; } }
        #endregion

        #region Init & Setup
        public override void onCustomInit()
        {
            if (AllFutures.Count != 2)
            {
                SwitchRunningState(ERunningState.InitFailed);
            }

            _futureA = AllFutures[0];
            _futureB = AllFutures[1];
            _instrumentA = _futureA.ID;
            _instrumentB = _futureB.ID;

            StrategyExHelper.PrintRunningDate(this);

            setupDeadline();
            setupHedgeStatusTracer();

            SwitchRunningState(ERunningState.DurationWatch);
        }

        // 根据配置的参数来设置策略截止期限
        private void setupDeadline()
        {
            DateTime preDay = TradingDayHelper.GetPreTradingDay(TradingDate);
            _deadlineWatching = UtilsEx.CalcDeadline(TradingDate, preDay, _tradeSlices, UtilsEx.DeadlineDir.ByBegin, DurationWatchChangeInHours * 60);
            _deadlineStopRunningIfPositionEmpty = UtilsEx.CalcDeadline(TradingDate, preDay, _tradeSlices, UtilsEx.DeadlineDir.ByEnd, DurationTerminalRunningIfPositionEmptyInHours * 60);
            _deadlineUnconditionLiquidate = UtilsEx.CalcDeadline(TradingDate, preDay, _tradeSlices, UtilsEx.DeadlineDir.ByEnd, DurationUnconditionLiquidateInMinutes);
            _deadlineTryLiquidate = UtilsEx.CalcDeadline(TradingDate, preDay, _tradeSlices, UtilsEx.DeadlineDir.ByEnd, DurationTryLiquidateInHours * 60);

            _timerWatching = new DeadlineTimer(_deadlineWatching, onDeadlineWatchingTicking, onDeadlineWatchingHit);
            _timerStopRunning = new DeadlineTimer(_deadlineStopRunningIfPositionEmpty, null, onDeadlineStopRunningHit);
            _timerTryLiquidate = new DeadlineTimer(_deadlineTryLiquidate, null, onDeadlineTryLiquidateHit);
            _timerUnconditionLiquidate = new DeadlineTimer(_deadlineUnconditionLiquidate, null, onDeadlineUnconditionLiquidateHit);

            Print("截止时间->观察: " + _deadlineWatching);
            Print("截止时间->平仓停止策略运行如果距离收盘时间小于" + DurationTerminalRunningIfPositionEmptyInHours + ": " + _deadlineStopRunningIfPositionEmpty);
            Print("截止时间->无条件平仓如果距离收盘时间小于" + DurationUnconditionLiquidateInMinutes + ": " + _deadlineUnconditionLiquidate);
            Print("截止时间->尝试平仓如果距离收盘时间小于" + DurationTryLiquidateInHours + ": " + _deadlineTryLiquidate);
        }

        // 初始化对冲状态跟踪器
        private void setupHedgeStatusTracer()
        {
            _orderStatusTracerA = new HedgeOrderTracer(this, _futureA, HandsOfA * LeverageA);
            _orderStatusTracerB = new HedgeOrderTracer(this, _futureB, HandsOfB * LeverageB);

            // 注册订单状态变化的回调事件
            _onOrderStatusChange += _orderStatusTracerA.OnOrderStatusChange;
            _onOrderStatusChange += _orderStatusTracerB.OnOrderStatusChange;
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
                _tradeA = _tradeB = null;
            }

            if (PrintPositionStatusOnTradeDeal) StrategyExHelper.PrintPositionStatus(this);
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
                _orderStatusTracerA.OpenPosition(MarketOrderType.Bear, _tickA.LastPrice, OrderPriceDiff);
                _orderStatusTracerB.OpenPosition(MarketOrderType.Bull, _tickB.LastPrice, OrderPriceDiff);
            }
            else
            {
                _orderStatusTracerA.OpenPosition(MarketOrderType.Bull, _tickA.LastPrice, OrderPriceDiff);
                _orderStatusTracerB.OpenPosition(MarketOrderType.Bear, _tickB.LastPrice, OrderPriceDiff);
            }
        }

        private void onLiquidatePosition()
        {
            _orderStatusTracerA.ClosePosition(_tickA.LastPrice, OrderPriceDiff);
            _orderStatusTracerB.ClosePosition(_tickB.LastPrice, OrderPriceDiff);
        }
        #endregion

        #region Timer handler
        private void onDeadlineWatchingHit(object evt)
        {
            calcWatchingResult();

            SwitchRunningState(ERunningState.RealTimeExchange);
        }

        private void onDeadlineWatchingTicking(object evt)
        {
            // 如果是tick驱动，算计该次的涨幅差，增加触发计时器。
            Tick tick = evt as Tick;
            if (tick != null)
            {
                _countOfChangeInWatchingDuration++;
                _totalChangePercentage += Math.Abs(getChangePercentage(true));
                return;
            }
        }

        private void onDeadlineTryLiquidateHit(object evt)
        {
            if (!IsPositionEmpty)
            {
                SwitchRunningState(ERunningState.TryLiquidate);
                return;
            }

            Print("尝试平仓， 但仓位为空！！！！！！！");
        }


        private void onDeadlineStopRunningHit(object evt)
        {
            if (IsPositionEmpty)
            {
                stopRunning();
            }
        }

        private void onDeadlineUnconditionLiquidateHit(object evt)
        {
            SwitchRunningState(ERunningState.UnconditionLiquidate);
        }
        #endregion

        #region Mechine state handler
        private static void onActionInit(WatchAndBreakout st, object customData)
        {
            st.onCustomInit();
        }

        private static void onActionInitFailed(WatchAndBreakout st, object customData)
        {
            st.Print("对冲必须设置2支期货品种!");
        }

        private static void onActionWatchDuration(WatchAndBreakout st, object customData)
        {
            st._timerWatching.Update(st.CurrentTime, customData);
        }

        private static void onActionRealTimeExchange(WatchAndBreakout st, object customData)
        {
            st.onRealtimeExchange(customData);

            st._timerStopRunning.Update(st.CurrentTime, customData);
            st._timerTryLiquidate.Update(st.CurrentTime, customData);
        }

        private static void onTryLiquidate(WatchAndBreakout st, object customData)
        {
            if (st.isProfitCoverCost())
            {
                st.SwitchRunningState(ERunningState.Stop);
                return;
            }

            st._timerUnconditionLiquidate.Update(st.CurrentTime, customData);
        }

        private static void onUnconditionLiquidate(WatchAndBreakout st, object customData)
        {
            st.onLiquidatePosition();

            st.SwitchRunningState(ERunningState.Stop);
        }

        private static void onStopRunning(WatchAndBreakout st, object customData)
        {
            st.stopRunning();
        }

        private void onRealtimeExchange(object customData)
        {
            Tick tick = customData as Tick;
            if (tick != null)
            {
                _tickA = LastFutureTick(_instrumentA);
                _tickB = LastFutureTick(_instrumentB);
                double deltaChange = getChangePercentage(true);
                double deltaChangeAbs = Math.Abs(deltaChange);

                if (IsPositionEmpty)
                {
                    if (deltaChangeAbs > _thresholdChangePercentage)
                    {
                        onOpenPosition(deltaChange);
                    }
                }
                else
                {
                    if (deltaChangeAbs < LiquidateChangePercentage)
                    {
                        onLiquidatePosition();
                    }
                }
            }
        }

        private bool isProfitCoverCost()
        {
            if (_orderStatusTracerA.Order == null || _orderStatusTracerB.Order == null) return true;

            double totalProfit = 0;
            PositionSeries ps = GetPosition(EnumMarket.期货, DefaultAccount);
            foreach(Position position in ps)
            {
                totalProfit += position.PositionProfit;
            }

            return totalProfit > 0;
        }

        private void stopRunning()
        {
            if (_hasStop) return;

            if (_runningState != ERunningState.Stop) SwitchRunningState(ERunningState.Stop);

            _hasStop = true;
        }

        public override void Exit()
        {
            Print("策略停止运行!" + CurrentTime);
            _orderStatusTracerA.PrintHitStatus();
            _orderStatusTracerB.PrintHitStatus();

            Order[] ods = new Order[]{_orderStatusTracerA.Order, _orderStatusTracerB.Order};
            StrategyExHelper.PrintPositionStatus(this, ods, true);
        }

        private void SwitchRunningState(ERunningState newState)
        {
            Print("===>>>>切换状态机:" + _RStranslater[(int)_runningState].Name + "---->" + _RStranslater[(int)newState].Name);

            _runningState = newState;
        }
        #endregion

        #region Mechine state defines
        // 状态
        public enum ERunningState
        {
            Init = 0,
            InitFailed, // 策略初始化失败
            DurationWatch, // 策略观察期间
            RealTimeExchange, // 观察结束，开始实时交易
            TryLiquidate,
            UnconditionLiquidate,
            Stop, // 策略停止运行
        }

        public class RunningStateTranslater
        {
            public ERunningState State;
            public string Name;

            public RunningStateTranslater(ERunningState state, string name)
            {
                State = state;
                Name = name;
            }
        }

        private static RunningStateTranslater[] _RStranslater = 
        {
            new RunningStateTranslater(ERunningState.Init, "策略初始化"),
            new RunningStateTranslater(ERunningState.InitFailed, "初始化失败"),
            new RunningStateTranslater(ERunningState.DurationWatch, "观察区间"),
            new RunningStateTranslater(ERunningState.RealTimeExchange, "实时交易"),
            new RunningStateTranslater(ERunningState.TryLiquidate, "尝试平仓"),
            new RunningStateTranslater(ERunningState.UnconditionLiquidate, "无条件平仓"),
            new RunningStateTranslater(ERunningState.Stop, "停止运行")
        };

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
