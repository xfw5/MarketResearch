using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Ats.Core;
using MarketResearch.Helper;

namespace MarketResearch.Extension
{
    // 对冲订单跟踪器
    public class HedgeOrderTracer
    {
        private StrategyEx _st;
        private Order _order;
        private Future _future;
        private int _volume;
        private MarketOrderType _marketType; // 市场方向

        private bool _orderLock; // 下单锁，锁住时表示下单后，等待订单返回
        private HedgeStatus _status;

        // 策略对冲总次数
        private int _openHitTimes = 0;
        private int _closeHitTimes = 0;

        // 其中，对冲失败次数
        private int _openFailedTimes = 0;
        private int _closeFailedTimes = 0;

        public bool IsPositionEmpty { get { return _status == HedgeStatus.WaitOpenPosition; } }
        public bool IsPositionOpen { get { return _status == HedgeStatus.WaitClosePosition; } }
        public Order Order { get { return _order; } }
        public Future Future { get { return _future; } }

        public HedgeOrderTracer(StrategyEx st, Future future, int volume)
        {
            _st = st;
            _future = future;
            _volume = volume;

            _orderLock = false;
            _status = HedgeStatus.WaitOpenPosition;

            _openHitTimes = 0;
            _closeHitTimes = 0;

            _openFailedTimes = 0;
            _closeFailedTimes = 0;
        }

        // 开仓
        public void OpenPosition(MarketOrderType marketType, double lastPrice, double priceDiff)
        {
            if (_orderLock || _status != HedgeStatus.WaitOpenPosition) return;

            _marketType = marketType;
            
            double priceLimite = getOpenPrice(lastPrice, priceDiff);
            EnumBuySell dir = getOpenPositionDir();
            _order = _st.SendOrder(_st.DefaultAccount, _future.ID, EnumMarket.期货, _future.ExchangeID,
                          priceLimite, _volume, dir, EnumOpenClose.开仓, EnumOrderPriceType.市价, 
                          EnumOrderTimeForce.当日有效, EnumHedgeFlag.投机);

            _orderLock = true;
            _status = HedgeStatus.WaitOpenOrderComplete;
            _openHitTimes++;

            _st.Print("===========》开仓：");
            OrderHelper.PrintOrderStatus(_st, _order);
        }

        // 对冲方向平仓
        public void ClosePosition(double lastPrice, double priceDiff)
        {
            if (_orderLock || _status != HedgeStatus.WaitClosePosition) return;

            double priceLimite = getClosePrice(lastPrice, priceDiff);
            EnumBuySell dir = getClosePositionDir();
            _order = _st.SendOrder(_st.DefaultAccount, _future.ID, EnumMarket.期货, _future.ExchangeID,
                          priceLimite, _volume, dir, EnumOpenClose.平今仓, EnumOrderPriceType.市价,
                          EnumOrderTimeForce.当日有效, EnumHedgeFlag.投机);

            _orderLock = true;
            _status = HedgeStatus.WaitCloseOrderComplete;
            _closeHitTimes++;

            _st.Print("===========》平今仓：");
            OrderHelper.PrintOrderStatus(_st, _order);
        }

        // 订单状态跟踪
        public void OnOrderStatusChange(Order order)
        {
            if (order != _order) return;

            _st.Print("==========订单变化===========");
            OrderHelper.PrintOrderStatus(_st, order);

            if (order.MQOrderStatus == EnumMQOrderStatus.全部成交)
            {
                if (_status == HedgeStatus.WaitOpenOrderComplete)
                {
                    _status = HedgeStatus.WaitClosePosition;
                }
                else if (_status == HedgeStatus.WaitCloseOrderComplete)
                {
                    _status = HedgeStatus.WaitOpenPosition;
                }

                _orderLock = false;
            }else if (order.MQOrderStatus == EnumMQOrderStatus.废单)
            {
                if (_status == HedgeStatus.WaitOpenOrderComplete)
                {
                    _openFailedTimes++;
                }else if (_status == HedgeStatus.WaitCloseOrderComplete)
                {
                    _closeFailedTimes++;
                }
            }
        }

        public void PrintHitStatus()
        {
            _st.Print("========》策略运行状态：" + _future.ID);
            _st.Print("开仓次数：" + _openHitTimes + " 失败次数：" + _openFailedTimes);
            _st.Print("平仓次数：" + _closeHitTimes + " 失败次数：" + _closeFailedTimes);
        }

        private double getOpenPrice(double lastPrice, double priceDiff)
        {
            if (_marketType == MarketOrderType.Bear) return lastPrice + _future.PriceTick * priceDiff;
            else return lastPrice - _future.PriceTick * priceDiff;
        }

        private double getClosePrice(double lastPrice, double priceDiff)
        {
            if (_marketType == MarketOrderType.Bear) return lastPrice - _future.PriceTick * priceDiff;
            else return lastPrice + _future.PriceTick * priceDiff;
        }

        private EnumBuySell getOpenPositionDir()
        {
            if (_marketType == MarketOrderType.Bear) return EnumBuySell.买入;
            else return EnumBuySell.卖出;
        }

        private EnumBuySell getClosePositionDir()
        {
            if (_marketType == MarketOrderType.Bear) return EnumBuySell.卖出;
            else return EnumBuySell.买入;
        }
    }

    public enum MarketOrderType
    {
        Bear = 0, // 空, 买入
        Bull // 多, 卖出
    }

    public enum HedgeStatus
    {
        WaitOpenPosition = 0, // 允许开仓
        WaitOpenOrderComplete, // 等待开仓订单下单完毕
        WaitClosePosition, // 允许平仓
        WaitCloseOrderComplete, // 等待平仓订单下单完毕
    }
}
