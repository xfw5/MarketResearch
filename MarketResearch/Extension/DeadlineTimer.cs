using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MarketResearch.Extension
{
    public class DeadlineTimer
    {
        public delegate void OnDeadlineDelegate(object evt);

        private bool _isDecay = false;
        private DateTime _deadline;
        private OnDeadlineDelegate _onHitCallbacks;
        private OnDeadlineDelegate _onTickingCallbacks;

        public bool IsDecay { get { return _isDecay; } }

        public DeadlineTimer(DateTime deadline, OnDeadlineDelegate ontickingHandler, OnDeadlineDelegate onHitCallback)
        {
            _deadline = deadline;

            if (ontickingHandler != null) _onTickingCallbacks += ontickingHandler;
            if (onHitCallback != null) _onHitCallbacks += onHitCallback;
        }

        public bool IsDeadlineHit(DateTime timeNow)
        {
            return timeNow >= _deadline;
        }

        public bool Update(DateTime timeNow, object evt)
        {
            if (IsDeadlineHit(timeNow))
            {
                if (_onHitCallbacks != null) _onHitCallbacks(evt);

                _isDecay = true;
                return true;
            }

            if (_onTickingCallbacks != null) _onTickingCallbacks(evt);

            return false;
        }
    }
}
