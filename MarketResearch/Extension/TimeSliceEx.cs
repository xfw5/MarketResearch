﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Ats.Core;

namespace MarketResearch.Extension
{
    public class TimeSliceEx
    {
        private TimeSlice _slice;
        private bool _isDayTrade = false;

        public bool IsDayTrade { get { return _isDayTrade; } }
        public TimeSlice Slice { get { return _slice; } }

        public static List<TimeSliceEx> CreateSlices(List<TimeSlice> slices)
        {
            List<TimeSliceEx> tse = new List<TimeSliceEx>();

            foreach(TimeSlice s in slices)
            {
                TimeSliceEx se = new TimeSliceEx(s);
                tse.Add(se);
            }

            return tse;
        }

        public static List<TimeSliceEx> CreateSlices(List<TimeSlice> slices, int dayTradeBegin, int dayTradeEnd)
        {
            List<TimeSliceEx> tse = new List<TimeSliceEx>();

            foreach (TimeSlice s in slices)
            {
                TimeSliceEx se = new TimeSliceEx(s);
                se.UpdateTradeType(dayTradeBegin, dayTradeEnd);
                tse.Add(se);
            }

            return tse;
        }

        public TimeSliceEx(TimeSlice slice)
        {
            _slice = slice;
        }

        public TimeSliceEx(TimeSlice slice, int dayTradeBegin, int dayTradeEnd)
        {
            _slice = slice;

            UpdateTradeType(dayTradeBegin, dayTradeEnd);
        }

        public void UpdateTradeType(int dayTradeBegin, int dayTradeEnd)
        {
            double db = dayTradeBegin * 60 * 60;
            double de = dayTradeEnd * 60 * 60;

            double sb = _slice.BeginTime.TotalSeconds;
            //double se = _slice.BeginTime.TotalSeconds;

            if (sb > db && sb < de)
            {
                _isDayTrade = true;
            }
        }
    }

    public class TimeSliceComparer : IComparer<TimeSliceEx>
    {
        private double _oneDay = 24 * 60 * 60;
        private double _dayBreak = 6 * 60 * 60;

        public TimeSliceComparer(double dayBreak = 6 * 60 * 60)
        {
            _dayBreak = dayBreak;
        }

        public int Compare(TimeSliceEx x, TimeSliceEx y)
        {
            double tx = x.Slice.BeginTime.TotalSeconds;
            double ty = y.Slice.BeginTime.TotalSeconds;

            if (x.IsDayTrade && y.IsDayTrade) return tx.CompareTo(ty);
            if (!x.IsDayTrade && !y.IsDayTrade)
            {

                if (tx < _dayBreak) tx += _oneDay;
                if (ty < _dayBreak) ty += _oneDay;

                return tx.CompareTo(ty);
            }

            if (!x.IsDayTrade) tx = -tx;
            if (!y.IsDayTrade) ty = -ty;

            return tx.CompareTo(ty);
        }
    }
}
