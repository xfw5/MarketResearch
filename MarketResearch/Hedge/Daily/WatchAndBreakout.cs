using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Ats.Core;
using MarketResearch.Extension;

namespace MarketResearch
{
    public class WatchAndBreakout: StrategyEx
    {
        #region Param
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
        private string _instrumentA;
        private string _instrumentB;

        private RunningState _runningState = RunningState.InitFailed;
        private static StgStateMechine[] _stateHandler = new[] 
        {
            new StgStateMechine(RunningState.InitFailed, onActionInitFailed),
            new StgStateMechine(RunningState.DurationWatch, OnActionWatchDuration), 
        };

        private DateTime _deadlineWatching;
        private DateTime _deadlineStopRunning;
        private DateTime _deadlineUnconditionLiquidate;
        private DateTime _deadlineTryLiquidate;
        #endregion

        public override void Init()
        {
            if (!CustomInit()) return;

            if (AllFutures.Count != 2)
            {
                _runningState = RunningState.InitFailed;
            }

            _instrumentA = AllFutures[0].ID;
            _instrumentB = AllFutures[1].ID;

            PrintRunningDate();

            setupDeadline();

            _runningState = RunningState.DurationWatch;
        }

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


        public override void OnTick(Tick tick)
        {
            Print("--->" + tick.TimeNow.ToString());
            Print(tick.DateTime.ToString());
        }


        public override void OnBar(Bar bar)
        {
            _stateHandler[(int)_runningState].Handler(this);
        }

        private static void onActionInitFailed(WatchAndBreakout st)
        {
            st.Print("Future's count must be 2. Please check and try it again!");
        }

        private static void OnActionWatchDuration(WatchAndBreakout st)
        {
            st.PrintRunningDate();
        }

        private static void OnActionStopRunning(WatchAndBreakout st)
        {
            st.Print("Stop running strategy require received, exit now...");
            st.Exit();
        }

        public enum RunningState
        {
            InitFailed = 0,
            DurationWatch,
            Stop,
        }

        public class StgStateMechine
        {
            public delegate void OnStateHandlerFunc(WatchAndBreakout stg);

            public RunningState State;
            public OnStateHandlerFunc Handler;

            public StgStateMechine(RunningState state, OnStateHandlerFunc handler)
            {
                this.State = state;
                this.Handler = handler;
            }
        }
    }
}
