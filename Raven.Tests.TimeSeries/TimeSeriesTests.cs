﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Raven.Abstractions.TimeSeries;
using Raven.Database.TimeSeries;
using Voron;
using Xunit;

namespace Raven.Tests.TimeSeries
{
	public class TimeSeriesTests : TimeSeriesTest
	{
		[Fact]
		public void CanQueryData()
		{
			using (var tss = GetStorage())
			{
				WriteTestData(tss);

				var start = new DateTimeOffset(2015, 4, 1, 0, 0, 0, TimeSpan.Zero);
				using (var r = tss.CreateReader())
				{
					var time = r.GetPoints("Simple", "Time", start.AddYears(-1), start.AddYears(1)).ToArray();
					var money = r.GetPoints("Simple", "Money", DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToArray();

					Assert.Equal(3, time.Length);
					Assert.Equal(new DateTimeOffset(2015, 4, 1, 0, 0, 0, TimeSpan.Zero), time[0].At);
					Assert.Equal(new DateTimeOffset(2015, 4, 1, 1, 0, 0, TimeSpan.Zero), time[1].At);
					Assert.Equal(new DateTimeOffset(2015, 4, 1, 2, 0, 0, TimeSpan.Zero), time[2].At);
					Assert.Equal(10, time[0].Values[0]);
					Assert.Equal(19, time[1].Values[0]);
					Assert.Equal(50, time[2].Values[0]);
#if DEBUG
					Assert.Equal("Time", time[0].DebugKey);
					Assert.Equal("Time", time[1].DebugKey);
					Assert.Equal("Time", time[2].DebugKey);
#endif
					
					Assert.Equal(3, money.Length);
#if DEBUG
					Assert.Equal("Money", money[0].DebugKey);
					Assert.Equal("Money", money[1].DebugKey);
					Assert.Equal("Money", money[2].DebugKey);
#endif
				}
			}
		}

		[Fact]
		public void CanQueryDataOnSeries()
		{
			using (var tss = GetStorage())
			{
				WriteTestData(tss);

				using (var r = tss.CreateReader())
				{
					var money = r.GetPoints("Simple", "Money", DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToArray();
					Assert.Equal(3, money.Length);
#if DEBUG
					Assert.Equal("Money", money[0].DebugKey);
					Assert.Equal("Money", money[1].DebugKey);
					Assert.Equal("Money", money[2].DebugKey);
#endif
				}
			}
		}

		[Fact]
		public void CanQueryDataInSpecificDurationSum()
		{
			using (var tss = GetStorage())
			{
				WriteTestData(tss);

				var start = new DateTimeOffset(2015, 4, 1, 0, 0, 0, TimeSpan.Zero);
				using (var r = tss.CreateReader())
				{
					var time = r.GetAggregatedPoints("Simple", "Time", start.AddDays(-1), start.AddDays(2), PeriodDuration.Hours(6)).ToArray();
					var money = r.GetAggregatedPoints("Simple", "Money", start.AddDays(-2), start.AddDays(1), PeriodDuration.Hours(2)).ToArray();

					Assert.Equal(12, time.Length);
					Assert.Equal(new DateTimeOffset(2015, 3, 31, 0, 0, 0, TimeSpan.Zero), time[0].StartAt);
					Assert.Equal(new DateTimeOffset(2015, 3, 31, 6, 0, 0, TimeSpan.Zero), time[1].StartAt);
					Assert.Equal(new DateTimeOffset(2015, 3, 31, 12, 0, 0, TimeSpan.Zero), time[2].StartAt);
					Assert.Equal(new DateTimeOffset(2015, 3, 31, 18, 0, 0, TimeSpan.Zero), time[3].StartAt);
					Assert.Equal(new DateTimeOffset(2015, 4, 1, 0, 0, 0, TimeSpan.Zero), time[4].StartAt);
					Assert.Equal(new DateTimeOffset(2015, 4, 1, 6, 0, 0, TimeSpan.Zero), time[5].StartAt);
					Assert.Equal(new DateTimeOffset(2015, 4, 1, 12, 0, 0, TimeSpan.Zero), time[6].StartAt);
					Assert.Equal(new DateTimeOffset(2015, 4, 1, 18, 0, 0, TimeSpan.Zero), time[7].StartAt);
					Assert.Equal(new DateTimeOffset(2015, 4, 2, 0, 0, 0, TimeSpan.Zero), time[8].StartAt);
					Assert.Equal(new DateTimeOffset(2015, 4, 2, 6, 0, 0, TimeSpan.Zero), time[9].StartAt);
					Assert.Equal(new DateTimeOffset(2015, 4, 2, 12, 0, 0, TimeSpan.Zero), time[10].StartAt);
					Assert.Equal(new DateTimeOffset(2015, 4, 2, 18, 0, 0, TimeSpan.Zero), time[11].StartAt);
					Assert.Equal(79, time[4].Value.Sum);
#if DEBUG
					Assert.Equal("Time", time[4].DebugKey);
#endif
					Assert.Equal(PeriodDuration.Hours(6), time[4].Duration);

					Assert.Equal(36, money.Length);
					for (int i = 0; i < 36; i++)
					{
#if DEBUG
						Assert.Equal("Money", money[i].DebugKey);
#endif
						Assert.Equal(PeriodDuration.Hours(2), money[0].Duration);
						if (i == 24 || i == 25)
							continue;
						Assert.Equal(0, money[i].Value.Sum);
						Assert.Equal(0, money[i].Value.Volume);
						Assert.Equal(0, money[i].Value.High);
					}
					Assert.Equal(600, money[24].Value.Sum);
					Assert.Equal(2, money[24].Value.Volume);
					Assert.Equal(130, money[25].Value.Sum);
					Assert.Equal(1, money[25].Value.Volume);
				}
			}
		}

		[Fact]
		public void CanQueryDataInSpecificDurationAverage()
		{
			using (var tss = GetStorage())
			{
				WriteTestData(tss);

				var start = new DateTimeOffset(2015, 4, 1, 0, 0, 0, TimeSpan.Zero);
				using (var r = tss.CreateReader())
				{
					var time = r.GetAggregatedPoints("Simple", "Time", start.AddMonths(-1), start.AddDays(1), PeriodDuration.Hours(3)).ToArray();
					var money = r.GetAggregatedPoints("Simple", "Money", start.AddDays(-1), start.AddMonths(1), PeriodDuration.Hours(2)).ToArray();

					Assert.Equal(24 / 3 * 32, time.Length);
					for (int i = 0; i < 256; i++)
					{
#if DEBUG
						Assert.Equal("Time", time[i].DebugKey);
#endif
						Assert.Equal(PeriodDuration.Hours(3), time[i].Duration);
						if (i == 248 || i == 249)
							continue;
						Assert.Equal(0, time[i].Value.Volume);
					}
					Assert.Equal("26.3333333333333", (time[248].Value.Sum / time[248].Value.Volume).ToString(CultureInfo.InvariantCulture));
					Assert.Equal(3, time[248].Value.Volume);
					Assert.Equal(10, time[248].Value.Open);
					Assert.Equal(50, time[248].Value.Close);
					Assert.Equal(50, time[248].Value.High);
					Assert.Equal(10, time[248].Value.Low);


					Assert.Equal(24 / 2 * 31, money.Length);
					for (int i = 0; i < 372; i++)
					{
						if (i == 12 || i == 13)
							continue;
						Assert.Equal(0, money[i].Value.Volume);
					}
#if DEBUG
					Assert.Equal("Money", money[12].DebugKey);
#endif
					Assert.Equal(300, money[12].Value.Sum / money[12].Value.Volume);
					Assert.Equal(130, money[13].Value.Sum / money[13].Value.Volume);
					Assert.Equal(PeriodDuration.Hours(2), money[12].Duration);
					Assert.Equal(PeriodDuration.Hours(2), money[13].Duration);
					Assert.Equal(2, money[12].Value.Volume);
					Assert.Equal(1, money[13].Value.Volume);
					Assert.Equal(54, money[12].Value.Open);
					Assert.Equal(130, money[13].Value.Open);
					Assert.Equal(546, money[12].Value.Close);
					Assert.Equal(130, money[13].Value.Close);
					Assert.Equal(54, money[12].Value.Low);
					Assert.Equal(130, money[13].Value.Low);
					Assert.Equal(546, money[12].Value.High);
					Assert.Equal(130, money[13].Value.High);
				}
			}
		}

		[Fact]
		public void CanQueryDataInSpecificDurationAverageUpdateRollup()
		{
			using (var tss = GetStorage())
			{
				WriteTestData(tss);

				var start = new DateTimeOffset(2015, 4, 1, 0, 0, 0, TimeSpan.Zero);
				using (var r = tss.CreateReader())
				{
					var time = r.GetAggregatedPoints("Simple", "Time", start.AddMonths(-1), start.AddDays(1), PeriodDuration.Hours(3)).ToArray();
					var money = r.GetAggregatedPoints("Simple", "Money", start.AddDays(-1), start.AddMonths(1), PeriodDuration.Hours(2)).ToArray();

					Assert.Equal(24/3*32, time.Length);
					for (int i = 0; i < 256; i++)
					{
						if (i == 248)
							continue;
						Assert.Equal(0, time[i].Value.Volume);
					}
					Assert.Equal("26.3333333333333", (time[248].Value.Sum / time[248].Value.Volume).ToString(CultureInfo.InvariantCulture));
#if DEBUG
					Assert.Equal("Time", time[248].DebugKey);
#endif
					Assert.Equal(PeriodDuration.Hours(3), time[248].Duration);
					Assert.Equal(3, time[248].Value.Volume);
					Assert.Equal(10, time[248].Value.Open);
					Assert.Equal(50, time[248].Value.Close);
					Assert.Equal(50, time[248].Value.High);
					Assert.Equal(10, time[248].Value.Low);

					Assert.Equal(24/2*31, money.Length);
					for (int i = 0; i < 372; i++)
					{
						if (i == 12 || i == 13)
							continue;
						Assert.Equal(0, money[i].Value.Volume);
					}
#if DEBUG
					Assert.Equal("Money", money[12].DebugKey);
#endif
					Assert.Equal(300, money[12].Value.Sum / money[12].Value.Volume);
					Assert.Equal(130, money[13].Value.Sum / money[13].Value.Volume);
					Assert.Equal(PeriodDuration.Hours(2), money[12].Duration);
					Assert.Equal(PeriodDuration.Hours(2), money[13].Duration);
					Assert.Equal(2, money[12].Value.Volume);
					Assert.Equal(1, money[13].Value.Volume);
					Assert.Equal(54, money[12].Value.Open);
					Assert.Equal(130, money[13].Value.Open);
					Assert.Equal(546, money[12].Value.Close);
					Assert.Equal(130, money[13].Value.Close);
					Assert.Equal(54, money[12].Value.Low);
					Assert.Equal(130, money[13].Value.Low);
					Assert.Equal(546, money[12].Value.High);
					Assert.Equal(130, money[13].Value.High);
				}

				var start2 = start.AddMonths(-1).AddDays(5);
				using (var writer = tss.CreateWriter())
				{
					int value = 6;
					for (int i = 0; i < 4; i++)
					{
						writer.Append("Simple", "Time", start2.AddHours(2 + i), value++);
						writer.Append("Simple", "Is", start2.AddHours(3 + i), value++);
						writer.Append("Simple", "Money", start2.AddHours(3 + i), value++);
					}
					writer.Commit();
				}

				using (var r = tss.CreateReader())
				{
					var time = r.GetAggregatedPoints("Simple", "Time", start.AddMonths(-1), start.AddDays(1), PeriodDuration.Hours(3)).ToArray();
					var money = r.GetAggregatedPoints("Simple", "Money", start.AddMonths(-2).AddDays(-1), start.AddMonths(2), PeriodDuration.Hours(2)).ToArray();

					Assert.Equal(24/3*32, time.Length);
					for (int i = 0; i < 256; i++)
					{
#if DEBUG
						Assert.Equal("Time", time[i].DebugKey);
#endif
						Assert.Equal(PeriodDuration.Hours(3), time[i].Duration);
						if (i == 40 ||i == 41 || i == 248)
							continue;
						Assert.Equal(0, time[i].Value.Volume);
					}
					
					Assert.Equal(1, time[40].Value.Volume);
					Assert.Equal(6, time[40].Value.Sum);
					Assert.Equal(6, time[40].Value.Open);
					Assert.Equal(6, time[40].Value.Close);
					Assert.Equal(6, time[40].Value.High);
					Assert.Equal(6, time[40].Value.Low);

					Assert.Equal(3, time[41].Value.Volume);
					Assert.Equal(36, time[41].Value.Sum);
					Assert.Equal(9, time[41].Value.Open);
					Assert.Equal(15, time[41].Value.Close);
					Assert.Equal(15, time[41].Value.High);
					Assert.Equal(9, time[41].Value.Low);

					Assert.Equal("26.3333333333333", (time[248].Value.Sum / time[248].Value.Volume).ToString(CultureInfo.InvariantCulture));
					Assert.Equal(3, time[248].Value.Volume);
					Assert.Equal(79, time[248].Value.Sum);
					Assert.Equal(10, time[248].Value.Open);
					Assert.Equal(50, time[248].Value.Close);
					Assert.Equal(50, time[248].Value.High);
					Assert.Equal(10, time[248].Value.Low);


					Assert.Equal((60 + 1 + 60) * 24 / 2, money.Length);
					for (int i = 0; i < 1452; i++)
					{
#if DEBUG
						Assert.Equal("Money", money[i].DebugKey);
#endif
						Assert.Equal(PeriodDuration.Hours(2), money[i].Duration);
						if (i == 409 || i == 410 || i == 411 || i == 720 || i == 721)
							continue;
						
						Assert.Equal(0, money[i].Value.Volume);
					}
					Assert.Equal(1, money[409].Value.Volume);
					Assert.Equal(2, money[410].Value.Volume);
					Assert.Equal(1, money[411].Value.Volume);
					Assert.Equal(8, money[409].Value.Sum);
					Assert.Equal(8, money[409].Value.Open);
					Assert.Equal(8, money[409].Value.Close);
					Assert.Equal(8, money[409].Value.Low);
					Assert.Equal(8, money[409].Value.High);
					Assert.Equal(25, money[410].Value.Sum);
					Assert.Equal(11, money[410].Value.Open);
					Assert.Equal(14, money[410].Value.Close);
					Assert.Equal(11, money[410].Value.Low);
					Assert.Equal(14, money[410].Value.High);
					Assert.Equal(17, money[411].Value.Sum);
					Assert.Equal(17, money[411].Value.Open);
					Assert.Equal(17, money[411].Value.Close);
					Assert.Equal(17, money[411].Value.Low);
					Assert.Equal(17, money[411].Value.High);

					Assert.Equal(300, money[720].Value.Sum / money[720].Value.Volume);
					Assert.Equal(130, money[721].Value.Sum / money[721].Value.Volume);
					Assert.Equal(2, money[720].Value.Volume);
					Assert.Equal(1, money[721].Value.Volume);
					Assert.Equal(54, money[720].Value.Open);
					Assert.Equal(130, money[721].Value.Open);
					Assert.Equal(546, money[720].Value.Close);
					Assert.Equal(130, money[721].Value.Close);
					Assert.Equal(54, money[720].Value.Low);
					Assert.Equal(130, money[721].Value.Low);
					Assert.Equal(546, money[720].Value.High);
					Assert.Equal(130, money[721].Value.High);
				}
			}
		}

		[Fact]
		public void CanQueryDataInSpecificDuration_LowerDurationThanActualOnDisk()
		{
			using (var tss = GetStorage())
			{
				WriteTestData(tss);

				var start = new DateTimeOffset(2015, 4, 1, 0, 0, 0, TimeSpan.Zero);
				using (var r = tss.CreateReader())
				{
					var time = r.GetAggregatedPoints("Simple", "Time", start.AddHours(-1), start.AddHours(4), PeriodDuration.Seconds(3)).ToArray();
					var money = r.GetAggregatedPoints("Simple", "Money", start.AddHours(-1), start.AddHours(4), PeriodDuration.Minutes(3)).ToArray();

					Assert.Equal(5 * 60 * 60 / 3, time.Length);
					for (int i = 0; i < 6000; i++)
					{
						if (i == 1200 || i == 2400 || i == 3600)
							continue;
						Assert.Equal(PeriodDuration.Seconds(3), time[i].Duration);
#if DEBUG
						Assert.Equal("Time", time[i].DebugKey);
#endif
						Assert.Equal(0, time[i].Value.Volume);
						Assert.Equal(0, time[i].Value.Open);
						Assert.Equal(0, time[i].Value.Close);
						Assert.Equal(0, time[i].Value.Low);
						Assert.Equal(0, time[i].Value.High);
						Assert.Equal(0, time[i].Value.Sum);
						Assert.Equal(0, time[i].Value.Volume);
					}
#if DEBUG
					Assert.Equal("Time", time[1200].DebugKey);
#endif
					Assert.Equal(PeriodDuration.Seconds(3), time[1200].Duration);
					Assert.Equal(PeriodDuration.Seconds(3), time[2400].Duration);
					Assert.Equal(PeriodDuration.Seconds(3), time[3600].Duration);
					Assert.Equal(1, time[1200].Value.Volume);
					Assert.Equal(1, time[2400].Value.Volume);
					Assert.Equal(1, time[3600].Value.Volume);
					Assert.Equal(10, time[1200].Value.Open);
					Assert.Equal(19, time[2400].Value.Open);
					Assert.Equal(50, time[3600].Value.Open);
					Assert.Equal(10, time[1200].Value.Close);
					Assert.Equal(19, time[2400].Value.Close);
					Assert.Equal(50, time[3600].Value.Close);
					Assert.Equal(10, time[1200].Value.Low);
					Assert.Equal(19, time[2400].Value.Low);
					Assert.Equal(50, time[3600].Value.Low);
					Assert.Equal(10, time[1200].Value.High);
					Assert.Equal(19, time[2400].Value.High);
					Assert.Equal(50, time[3600].Value.High);
					Assert.Equal(10, time[1200].Value.Sum);
					Assert.Equal(19, time[2400].Value.Sum);
					Assert.Equal(50, time[3600].Value.Sum);
					Assert.Equal(1, time[1200].Value.Volume);
					Assert.Equal(1, time[2400].Value.Volume);
					Assert.Equal(1, time[3600].Value.Volume);


					Assert.Equal(5 * 60 / 3, money.Length);
					for (int i = 0; i < 100; i++)
					{
#if DEBUG
						Assert.Equal("Money", money[i].DebugKey);
#endif
						Assert.Equal(PeriodDuration.Minutes(3), money[i].Duration);
						if (i == 20 || i == 40 || i == 60)
							continue;

						Assert.Equal(0, money[i].Value.Volume);
						Assert.Equal(0, money[i].Value.Open);
						Assert.Equal(0, money[i].Value.Close);
						Assert.Equal(0, money[i].Value.Low);
						Assert.Equal(0, money[i].Value.High);
						Assert.Equal(0, money[i].Value.Sum);
						Assert.Equal(0, money[i].Value.Volume);
					}
					Assert.Equal(1, money[20].Value.Volume);
					Assert.Equal(1, money[40].Value.Volume);
					Assert.Equal(1, money[60].Value.Volume);
					Assert.Equal(54, money[20].Value.Open);
					Assert.Equal(546, money[40].Value.Open);
					Assert.Equal(130, money[60].Value.Open);
					Assert.Equal(54, money[20].Value.Close);
					Assert.Equal(546, money[40].Value.Close);
					Assert.Equal(130, money[60].Value.Close);
					Assert.Equal(54, money[20].Value.Low);
					Assert.Equal(546, money[40].Value.Low);
					Assert.Equal(130, money[60].Value.Low);
					Assert.Equal(54, money[20].Value.High);
					Assert.Equal(546, money[40].Value.High);
					Assert.Equal(130, money[60].Value.High);
					Assert.Equal(54, money[20].Value.Sum);
					Assert.Equal(546, money[40].Value.Sum);
					Assert.Equal(130, money[60].Value.Sum);
					Assert.Equal(1, money[20].Value.Volume);
					Assert.Equal(1, money[40].Value.Volume);
					Assert.Equal(1, money[60].Value.Volume);
				}
			}
		}

		[Fact]
		public void MissingDataInSeries()
		{
			using (var tss = GetStorage())
			{
				WriteTestData(tss);

				var start = new DateTimeOffset(2015, 4, 1, 0, 0, 0, TimeSpan.Zero);
				using (var r = tss.CreateReader())
				{
					var time = r.GetPoints("Simple", "Time", start.AddSeconds(1), start.AddMinutes(30)).ToArray();
					var money = r.GetPoints("Simple", "Money", start.AddSeconds(1), start.AddMinutes(30)).ToArray();
					var Is = r.GetPoints("Simple", "Is", start.AddSeconds(1), start.AddMinutes(30)).ToArray();

					Assert.Equal(0, time.Length);
					Assert.Equal(0, money.Length);
					Assert.Equal(0, Is.Length);
				}
			}
		}

		private static void WriteTestData(TimeSeriesStorage tss)
		{
			var start = new DateTimeOffset(2015, 4, 1, 0, 0, 0, TimeSpan.Zero);
			var data = new[]
			{
				new {Key = "Time", At = start, Value = 10},
				new {Key = "Money", At = start, Value = 54},
				new {Key = "Is", At = start, Value = 1029},

				new {Key = "Money", At = start.AddHours(1), Value = 546},
				new {Key = "Is", At = start.AddHours(1), Value = 70},
				new {Key = "Time", At = start.AddHours(1), Value = 19},

				new {Key = "Is", At = start.AddHours(2), Value = 64},
				new {Key = "Money", At = start.AddHours(2), Value = 130},
				new {Key = "Time", At = start.AddHours(2), Value = 50},
			};

			var writer = tss.CreateWriter();
			foreach (var item in data)
			{
				writer.Append("Simple", item.Key, item.At, item.Value);
			}

			writer.Commit();
			writer.Dispose();
		}
	}
}