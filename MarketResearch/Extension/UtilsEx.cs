using Ats.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MarketResearch.Extension
{
    public class UtilsEx
    {
        public static bool IsHit(double input, double measure, double tolerance)
        {
            return Math.Abs(input - measure) < tolerance;
        }

        public static DateTime MakeDateByDay(DateTime tradeDate, TimeSpan span)
        {
            return new DateTime(tradeDate.Year, tradeDate.Month, tradeDate.Day, span.Hours, span.Minutes, span.Seconds);
        }

        public static DateTime MakeDateByNight(DateTime tradeDate, TimeSlice slice)
        {
            DateTime preTradingDay = TradingDayHelper.GetPreTradingDay(tradeDate);
            DateTime secondDayofPreTradingDay = preTradingDay.AddDays(1);

            return TradingDayHelper.GetNaturalDateTime(slice.EndTime,
                              tradeDate, preTradingDay, secondDayofPreTradingDay);
        }

        public static DateTime CalcDeadline(DateTime date, DateTime preDate, List<TimeSliceEx> slices, DeadlineDir dir, double deadlineTimeInMinutes)
        {
            double minLeap = deadlineTimeInMinutes;
            TimeSlice slice = null;
            double totalMinutes = 0;

            if (dir == DeadlineDir.ByBegin)
            {
                for (int i = 0; i < slices.Count; i++)
                {
                    slice = slices[i].Slice;
                    totalMinutes = Math.Abs(slice.Duration.TotalMinutes);
                    if (minLeap > totalMinutes)
                    {
                        minLeap -= totalMinutes;
                    }
                    else
                    {
                        TimeSpan dts = slice.BeginTime.Add(TimeSpan.FromMinutes(minLeap));
                        if (!slices[i].IsDayTrade) return new DateTime(preDate.Year, preDate.Month, preDate.Day, dts.Hours, dts.Minutes, dts.Seconds);
                        else return new DateTime(date.Year, date.Month, date.Day, dts.Hours, dts.Minutes, dts.Seconds);
                    }
                }
            }
            else
            {
                for (int i = slices.Count - 1; i >= 0; i--)
                {
                    slice = slices[i].Slice;
                    totalMinutes = Math.Abs(slice.Duration.TotalMinutes);
                    if (minLeap > totalMinutes)
                    {
                        minLeap -= totalMinutes;
                    }
                    else
                    {
                        TimeSpan dts = slice.EndTime.Subtract(TimeSpan.FromMinutes(minLeap));
                        if (!slices[i].IsDayTrade) return new DateTime(preDate.Year, preDate.Month, preDate.Day, dts.Hours, dts.Minutes, dts.Seconds);
                        else return new DateTime(date.Year, date.Month, date.Day, dts.Hours, dts.Minutes, dts.Seconds);
                    }
                }
            }

            return date;
        }

        public enum DeadlineDir
        {
            ByEnd = 0,
            ByBegin
        }
    }
}
