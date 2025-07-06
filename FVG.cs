using System;
using cAlgo.API;
using cAlgo.API.Indicators;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API.Internals;

namespace cAlgo.Indicators
{
    [Indicator(IsOverlay = true, TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class FVG : Indicator
    {
        [Parameter("Bullish Color", DefaultValue = "DodgerBlue", Group = "FVG Display")]
        public string BullishColorString { get; set; }

        [Parameter("Bearish Color", DefaultValue = "Tomato", Group = "FVG Display")]
        public string BearishColorString { get; set; }

        [Parameter("Mitigated Color", DefaultValue = "Gray", Group = "FVG Display")]
        public string MitigatedColorString { get; set; }

        [Parameter("Fill Boxes", DefaultValue = true, Group = "FVG Display")]
        public bool FillBoxes { get; set; }

        [Parameter("Extend Boxes", DefaultValue = true, Group = "FVG Display")]
        public bool ExtendBoxes { get; set; }

        [Parameter("Box Opacity", DefaultValue = 50, MinValue = 0, MaxValue = 255, Group = "FVG Display")]
        public int BoxOpacity { get; set; }
        
        [Parameter("Hide Mitigated", DefaultValue = true, Group = "FVG Behavior")]
        public bool HideMitigated { get; set; }

        [Parameter("Max FVGs to Show", DefaultValue = 10, MinValue = 1, Group = "FVG Behavior")]
        public int MaxFvgsToShow { get; set; }
        
        [Parameter("Min FVG Size", DefaultValue = 0.0, MinValue = 0, Group = "FVG Behavior")]
        public double MinFvgSize { get; set; }

        [Parameter("Show Daily Lines", DefaultValue = true, Group = "Daily Lines")]
        public bool ShowDailyLines { get; set; }

        [Parameter("Session Reset Hour (UTC)", DefaultValue = 22, MinValue = 0, MaxValue = 23, Group = "Daily Lines")]
        public int SessionResetHour { get; set; }
        
        [Parameter("High Line Color", DefaultValue = "Green", Group = "Daily Lines")]
        public string HighLineColorString { get; set; }

        [Parameter("Low Line Color", DefaultValue = "Red", Group = "Daily Lines")]
        public string LowLineColorString { get; set; }

        [Parameter("Line Thickness", DefaultValue = 1, MinValue = 1, MaxValue = 5, Group = "Daily Lines")]
        public int DailyLineThickness { get; set; }

        [Parameter("Line Style", DefaultValue = LineStyle.Dots, Group = "Daily Lines")]
        public LineStyle DailyLineStyle { get; set; }

        // --- Private FVG Variables ---
        private Color _bullishColor;
        private Color _bearishColor;
        private Color _mitigatedColor;
        private readonly List<FairValueGap> _activeFvgs = new List<FairValueGap>();
        private const string FvgObjectPrefix = "FVG_";

        // --- Private Daily Lines Variables ---
        private double _dailyHigh;
        private double _dailyLow;
        private DateTime _currentSessionDate;
        private DateTime _sessionStartBarTime;
        private Color _highLineColor;
        private Color _lowLineColor;
        private const string DailyHighName = "DailyHighLine";
        private const string DailyLowName = "DailyLowLine";

        // --- FVG Data Structure ---
        private class FairValueGap
        {
            public double TopPrice { get; set; }
            public double BottomPrice { get; set; }
            public double OriginalTopPrice { get; set; }
            public double OriginalBottomPrice { get; set; }
            public bool IsBullish { get; set; }
            public int StartBarIndex { get; set; }
            public string RectangleName { get; set; }
            public bool IsMitigated { get; set; }
            public int MitigatedBarIndex { get; set; }
        }

        protected override void Initialize()
        {
            _bullishColor = Color.FromName(BullishColorString);
            _bearishColor = Color.FromName(BearishColorString);
            _mitigatedColor = Color.FromName(MitigatedColorString);
            _highLineColor = Color.FromName(HighLineColorString);
            _lowLineColor = Color.FromName(LowLineColorString);
        }

        public override void Calculate(int index)
        {
            // --- Daily Lines Logic ---
            UpdateDailyLines(index);
            
            // --- FVG Logic ---
            // FVG detection and state updates should only be based on closed bars to prevent repainting.
            // We process the bar at index - 1, which has just closed.
            // We need 3 closed bars for a pattern (i-3, i-2, i-1), so index must be at least 3.
            if (index < 3)
            {
                // On early bars, still process drawing to clear any old objects if necessary
                ProcessFvgs(index);
                return;
            }

            int processingIndex = index - 1; // The most recently closed bar
            int fvgStartBarIndex = processingIndex - 1; // The middle bar of the potential FVG pattern

            // Check if an FVG originating from this 3-bar pattern already exists.
            if (!_activeFvgs.Any(f => f.StartBarIndex == fvgStartBarIndex))
            {
                var bar1 = Bars[processingIndex - 2];
                var bar3 = Bars[processingIndex];

                // Classic FVG Logic: Based on candle wicks (High/Low) of closed bars.
                if (bar1.High < bar3.Low)
                {
                    double fvgSize = bar3.Low - bar1.High;
                    if (fvgSize >= MinFvgSize)
                    {
                        var fvg = new FairValueGap
                        {
                            TopPrice = bar3.Low,
                            BottomPrice = bar1.High,
                            OriginalTopPrice = bar3.Low,
                            OriginalBottomPrice = bar1.High,
                            IsBullish = true,
                            StartBarIndex = fvgStartBarIndex,
                            RectangleName = $"{FvgObjectPrefix}Bull_{processingIndex}",
                            IsMitigated = false
                        };
                        _activeFvgs.Add(fvg);
                    }
                }
                else if (bar1.Low > bar3.High)
                {
                    double fvgSize = bar1.Low - bar3.High;
                    if (fvgSize >= MinFvgSize)
                    {
                        var fvg = new FairValueGap
                        {
                            TopPrice = bar1.Low,
                            BottomPrice = bar3.High,
                            OriginalTopPrice = bar1.Low,
                            OriginalBottomPrice = bar3.High,
                            IsBullish = false,
                            StartBarIndex = fvgStartBarIndex,
                            RectangleName = $"{FvgObjectPrefix}Bear_{processingIndex}",
                            IsMitigated = false
                        };
                        _activeFvgs.Add(fvg);
                    }
                }
            }
            
            ProcessFvgs(index); // Pass the current, real-time index for drawing purposes
        }
        
        private void ProcessFvgs(int currentIndex)
        {
            // --- Step 1: Update the state of all active FVGs based on closed bars ---
            // We only use closed bars for state changes to avoid repainting.
            // The last closed bar is at `currentIndex - 1`.
            if (currentIndex > 0)
            {
                int mitigationBarIndex = currentIndex - 1;
                var mitigationBar = Bars[mitigationBarIndex];

                foreach (var fvg in _activeFvgs)
                {
                    if (fvg.IsMitigated) continue;

                    // An FVG pattern is confirmed at the close of its 3rd bar (fvg.StartBarIndex + 1).
                    // Mitigation can only happen on subsequent bars.
                    // Therefore, the mitigationBarIndex must be after the FVG's 3rd bar.
                    if (mitigationBarIndex <= fvg.StartBarIndex + 1) continue;

                    var currentBar = mitigationBar; // Use the closed bar for logic
                    if (fvg.IsBullish)
                    {
                        // For a bullish FVG, the price moving down fills the gap (from top to bottom).
                        // Check if the current bar's low has entered the gap area.
                        if (currentBar.Low < fvg.TopPrice)
                        {
                            // If the low fills more than 90% of the gap, mark as fully mitigated.
                            // The 90% level is calculated based on the original FVG size.
                            double mitigationLevel = 0.1 * fvg.OriginalTopPrice + 0.9 * fvg.OriginalBottomPrice;
                            if (currentBar.Low <= mitigationLevel)
                            {
                                fvg.IsMitigated = true;
                                fvg.MitigatedBarIndex = mitigationBarIndex;
                            }
                            else
                            {
                                // 部分回补时更新顶部价格，但保持最小尺寸要求
                                double newTopPrice = currentBar.Low;
                                if (fvg.OriginalTopPrice - newTopPrice < MinFvgSize && MinFvgSize > 0)
                                {
                                    fvg.IsMitigated = true;
                                    fvg.MitigatedBarIndex = mitigationBarIndex;
                                }
                                else
                                {
                                    fvg.TopPrice = newTopPrice;
                                }
                            }
                        }
                    }
                    else // IsBearish
                    {
                        // For a bearish FVG, the price moving up fills the gap (from bottom to top).
                        // Check if the current bar's high has entered the gap area.
                        if (currentBar.High > fvg.BottomPrice)
                        {
                            // If the high fills more than 90% of the gap, mark as fully mitigated.
                            // The 90% level is calculated based on the original FVG size.
                            double mitigationLevel = 0.9 * fvg.OriginalTopPrice + 0.1 * fvg.OriginalBottomPrice;
                            if (currentBar.High >= mitigationLevel)
                            {
                                fvg.IsMitigated = true;
                                fvg.MitigatedBarIndex = mitigationBarIndex;
                            }
                            else
                            {
                                // 部分回补时更新底部价格，但保持最小尺寸要求
                                double newBottomPrice = currentBar.High;
                                if (newBottomPrice - fvg.OriginalBottomPrice < MinFvgSize && MinFvgSize > 0)
                                {
                                    fvg.IsMitigated = true;
                                    fvg.MitigatedBarIndex = mitigationBarIndex;
                                }
                                else
                                {
                                    fvg.BottomPrice = newBottomPrice;
                                }
                            }
                        }
                    }
                }
            }

            // --- Step 2: Determine which FVGs to display on the chart ---
            var fvgsToDisplay = _activeFvgs
                .Where(fvg => !fvg.IsMitigated || !HideMitigated) // Filter based on mitigation visibility
                .OrderByDescending(fvg => fvg.StartBarIndex)     // Get the most recent ones
                .Take(MaxFvgsToShow)                              // Apply the display limit
                .ToList();
            
            // --- Step 3: Clear all existing FVG drawings (Robust "Clear and Redraw") ---
            // This approach is the most reliable way to prevent state desynchronization issues.
            var fvgRects = Chart.FindAllObjects<ChartRectangle>();
            foreach (var rect in fvgRects)
            {
                if (rect.Name.StartsWith(FvgObjectPrefix))
                {
                    Chart.RemoveObject(rect.Name);
                }
            }

            // --- Step 4: Draw the currently valid FVGs ---
            foreach (var fvg in fvgsToDisplay)
            {
                var color = fvg.IsMitigated ? _mitigatedColor : (fvg.IsBullish ? _bullishColor : _bearishColor);
                var finalColor = Color.FromArgb(BoxOpacity, color);
                var startTime = Bars.OpenTimes[fvg.StartBarIndex];
                DateTime endTime;

                if (fvg.IsMitigated)
                {
                    if (fvg.MitigatedBarIndex < Bars.Count)
                        endTime = Bars.OpenTimes[fvg.MitigatedBarIndex];
                    else
                        endTime = Server.Time;
                }
                else if (ExtendBoxes)
                {
                    endTime = Server.Time.AddYears(1);
                }
                else
                {
                     // The FVG pattern is complete at the close of the 3rd bar (StartBarIndex + 1).
                     // To draw the box just for the pattern's duration, we should end it at the open of the *next* bar.
                     if (fvg.StartBarIndex + 2 < Bars.Count)
                        endTime = Bars.OpenTimes[fvg.StartBarIndex + 2];
                     else
                        endTime = Server.Time; // Fallback for the most recent bar
                }

                Chart.DrawRectangle(fvg.RectangleName, startTime, fvg.TopPrice, endTime, fvg.BottomPrice, finalColor, 1, LineStyle.Solid);
                
                var newRect = Chart.FindObject(fvg.RectangleName) as ChartRectangle;
                if (newRect != null)
                {
                    newRect.IsFilled = FillBoxes;
                }
            }
            
            // --- Step 5: Cleanup to prevent memory leaks ---
            // Periodically remove old, mitigated FVGs that are no longer in the display range.
            // This prevents the _activeFvgs list from growing indefinitely over long chart histories.
            if (_activeFvgs.Count > MaxFvgsToShow * 2) // Run this check only when the list is getting large
            {
                var allFvgsSorted = _activeFvgs.OrderByDescending(f => f.StartBarIndex).ToList();
                if (allFvgsSorted.Count > MaxFvgsToShow)
                {
                    // Find the bar index of the oldest FVG we would ever consider displaying.
                    int cutoffBarIndex = allFvgsSorted[MaxFvgsToShow - 1].StartBarIndex;
                    
                    // Remove any FVG that is mitigated and older than this cutoff.
                    // Unmitigated FVGs are kept regardless of age, as they might still be relevant.
                    _activeFvgs.RemoveAll(fvg => fvg.IsMitigated && fvg.StartBarIndex < cutoffBarIndex);
                }
            }
        }

        private void UpdateDailyLines(int index)
        {
            // Always remove existing lines first to ensure any parameter changes (color, style, etc.) are applied immediately.
            Chart.RemoveObject(DailyHighName);
            Chart.RemoveObject(DailyLowName);

            if (!ShowDailyLines)
            {
                return; // If disabled, we just want to ensure they are removed, which we've already done.
            }

            var currentBar = Bars[index];
            var sessionDate = GetSessionDate(currentBar.OpenTime, SessionResetHour);

            if (sessionDate != _currentSessionDate)
            {
                _currentSessionDate = sessionDate;
                _dailyHigh = currentBar.High;
                _dailyLow = currentBar.Low;
                _sessionStartBarTime = currentBar.OpenTime;
            }
            else
            {
                _dailyHigh = Math.Max(_dailyHigh, currentBar.High);
                _dailyLow = Math.Min(_dailyLow, currentBar.Low);
            }

            var lineEndTime = Server.Time.AddYears(1);

            Chart.DrawTrendLine(DailyHighName, _sessionStartBarTime, _dailyHigh, lineEndTime, _dailyHigh, _highLineColor, DailyLineThickness, DailyLineStyle);
            Chart.DrawTrendLine(DailyLowName, _sessionStartBarTime, _dailyLow, lineEndTime, _dailyLow, _lowLineColor, DailyLineThickness, DailyLineStyle);
        }

        private DateTime GetSessionDate(DateTime time, int resetHour)
        {
            return time.Hour < resetHour ? time.Date.AddDays(-1) : time.Date;
        }
    }
}