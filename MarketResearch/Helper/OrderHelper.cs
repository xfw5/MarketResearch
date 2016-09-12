using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Ats.Core;

namespace MarketResearch.Helper
{
    public class OrderHelper
    {
        public static void PrintOnTradeStatus(Strategy st, Trade trade)
        {
            st.Print(trade.ToString());
        }

        public static void PrintOrderStatus(Strategy st, Order order)
        {
            st.Print("订单状态:" + order.ToString());
        }
    }
}
