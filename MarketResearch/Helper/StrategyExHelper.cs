using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MarketResearch.Extension;
using Ats.Core;

namespace MarketResearch.Helper
{
    // 策略助手类，就是专门干一些琐碎的事情，好比助理.
    public class StrategyExHelper
    {
        public static double Change(Tick tick)
        {
            return (tick.LastPrice - tick.PreClosePrice) / tick.PreClosePrice * 100;
        }

        public static void PrintRuntimeStatus(StrategyEx se)
        {
            se.Print("有夜盘: " + se.HasNightTrade.ToString() +
                      " 当前盘: " + se.TradeType.ToString());

            if (se.CurrentSlice == null) se.Print("休盘.");
            else se.Print("当前交易时间片:" + se.CurrentSlice.ToString());
        }

        public static void PrintCurrentTimeSlice(StrategyEx se)
        {
            string msg = "休息";
            if (se.CurrentSlice != null) msg = se.CurrentSlice.ToString();

            se.Print("当前交易时间片:" + msg);
        }

        public static void PrintTradeType(StrategyEx se)
        {
            se.Print("当前盘:" + se.TradeType.ToString());
        }

        public static void PrintRunningDate(StrategyEx se)
        {
            se.Print("当前系统时间： " + se.Now.ToString());
            se.Print("========>交易所时间" + se.CurrentTime);
        }
    }
}
