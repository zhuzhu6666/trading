using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo
{
    public class OrderBlockInfo
    {
        public int Index { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public bool IsBullish { get; set; }
        public bool IsMitigated { get; set; }
        public string Name { get; set; }
        public ChartRectangle Rectangle { get; set; }
        public ChartText Label { get; set; }
    }

   
    /// 摇摆点数据结构，增加一个状态来防止重绘
    
    public class SwingPoint
    {
        public int Index { get; set; }
        public double Price { get; set; }
        public bool IsBroken { get; set; } // **核心改动：用状态标记代替删除**
    }

    [Indicator(IsOverlay = true, TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class OB : Indicator
    {
        [Parameter("Swing Period", DefaultValue = 5, MinValue = 2, Group = "SMC Parameters")]
        public int SwingPeriod { get; set; }

        [Parameter("Volume Threshold Multiplier", DefaultValue = 1.2, MinValue = 0.0, Group = "SMC Parameters")]
        public double VolumeThreshold { get; set; }

        [Parameter("Volume Lookback", DefaultValue = 20, MinValue = 2, Group = "SMC Parameters")]
        public int VolumeLookback { get; set; }

        [Parameter("Bullish OB Color", DefaultValue = "DodgerBlue", Group = "Display Settings")]
        public string BullishColorStr { get; set; }

        [Parameter("Bearish OB Color", DefaultValue = "OrangeRed", Group = "Display Settings")]
        public string BearishColorStr { get; set; }

        [Parameter("Mitigated OB Color", DefaultValue = "Gray", Group = "Display Settings")]
        public string MitigatedColorStr { get; set; }
        
        [Parameter("Show Volume Label", DefaultValue = true, Group = "Display Settings")]
        public bool ShowVolumeLabel { get; set; }

        [Parameter("Fill Opacity", DefaultValue = 40, MinValue = 0, MaxValue = 100, Group = "Display Settings")]
        public int FillOpacity { get; set; }
        
        [Parameter("Mitigated Extension (Bars)", DefaultValue = 1, MinValue = 1, MaxValue = 100, Group = "Display Settings")]
        public int MitigatedExtensionBars { get; set; }

        private SimpleMovingAverage _volumeSma;
        private readonly List<OrderBlockInfo> _activeOrderBlocks = new List<OrderBlockInfo>();
        private readonly List<SwingPoint> _swingHighs = new List<SwingPoint>();
        private readonly List<SwingPoint> _swingLows = new List<SwingPoint>();

        private Color _bullishColor;
        private Color _bearishColor;
        private Color _mitigatedColor;
        private int _lastCalculatedIndex = -1;

        protected override void Initialize()
        {
            _volumeSma = Indicators.SimpleMovingAverage(Bars.TickVolumes, VolumeLookback);
            _bullishColor = Color.FromName(BullishColorStr);
            _bearishColor = Color.FromName(BearishColorStr);
            _mitigatedColor = Color.FromName(MitigatedColorStr);
        }

        public override void Calculate(int index)
        {
            // 确保每个bar只在开盘时计算一次，极大提升性能
            if (_lastCalculatedIndex >= index -1 && !IsLastBar)
            {
                return;
            }

            // 对历史K线进行一次性回溯计算
            for (int i = _lastCalculatedIndex + 1; i < index; i++)
            {
                // 1. 识别并缓存已形成的摇摆点
                IdentifySwingPoints(i);
                // 2. 基于缓存的摇摆点检测市场结构突破和新的OB
                DetectNewOrderBlock(i);
                // 3. 检查是否有历史OB被当前K线缓解
                CheckForMitigation(i);
            }
            
            // 对实时K线，只检查缓解情况
            CheckForMitigation(index);

            _lastCalculatedIndex = index - 1;
        }

        private void IdentifySwingPoints(int index)
        {
            int checkIndex = index - SwingPeriod;
            if (checkIndex < SwingPeriod) return;

            // 检查摇摆高点
            bool isSwingHigh = true;
            double high = Bars.HighPrices[checkIndex];
            for (int i = 1; i <= SwingPeriod; i++)
            {
                if (Bars.HighPrices[checkIndex - i] >= high || Bars.HighPrices[checkIndex + i] > high)
                {
                    isSwingHigh = false;
                    break;
                }
            }
            if (isSwingHigh && !_swingHighs.Exists(s => s.Index == checkIndex))
            {
                _swingHighs.Add(new SwingPoint { Index = checkIndex, Price = high, IsBroken = false });
            }

            // 检查摇摆低点
            bool isSwingLow = true;
            double low = Bars.LowPrices[checkIndex];
            for (int i = 1; i <= SwingPeriod; i++)
            {
                if (Bars.LowPrices[checkIndex - i] <= low || Bars.LowPrices[checkIndex + i] < low)
                {
                    isSwingLow = false;
                    break;
                }
            }
            if (isSwingLow && !_swingLows.Exists(s => s.Index == checkIndex))
            {
                _swingLows.Add(new SwingPoint { Index = checkIndex, Price = low, IsBroken = false });
            }
        }
        
        private void DetectNewOrderBlock(int index)
        {
            var breakBar = Bars[index];

            // --- A. 检测看涨OB (因向上突破导致) ---
            SwingPoint lastSwingHigh = FindLastUnbrokenSwingHighBefore(index);
            if (lastSwingHigh != null && breakBar.Close > lastSwingHigh.Price)
            {
                int obIndex = FindLastBearishBar(index - 1, lastSwingHigh.Index);
                if (obIndex > 0 && Bars.TickVolumes[obIndex] > _volumeSma.Result[obIndex] * VolumeThreshold)
                {
                    var newOb = new OrderBlockInfo { Index = obIndex, High = Bars.HighPrices[obIndex], Low = Bars.LowPrices[obIndex], IsBullish = true, Name = $"BullOB_{obIndex}"};
                    _activeOrderBlocks.Add(newOb);
                    DrawOrderBlock(newOb);
                }
                // **核心改动**: 将所有被突破的摇摆点标记为失效，而不是删除
                foreach(var sh in _swingHighs)
                {
                    if(sh.Index <= lastSwingHigh.Index) sh.IsBroken = true;
                }
            }

            // --- B. 检测看跌OB (因向下突破导致) ---
            SwingPoint lastSwingLow = FindLastUnbrokenSwingLowBefore(index);
            if (lastSwingLow != null && breakBar.Close < lastSwingLow.Price)
            {
                int obIndex = FindLastBullishBar(index - 1, lastSwingLow.Index);
                if (obIndex > 0 && Bars.TickVolumes[obIndex] > _volumeSma.Result[obIndex] * VolumeThreshold)
                {
                    var newOb = new OrderBlockInfo { Index = obIndex, High = Bars.HighPrices[obIndex], Low = Bars.LowPrices[obIndex], IsBullish = false, Name = $"BearOB_{obIndex}"};
                    _activeOrderBlocks.Add(newOb);
                    DrawOrderBlock(newOb);
                }
                // **核心改动**: 将所有被突破的摇摆点标记为失效，而不是删除
                foreach(var sl in _swingLows)
                {
                    if(sl.Index <= lastSwingLow.Index) sl.IsBroken = true;
                }
            }
        }

        private void CheckForMitigation(int index)
        {
            for (int i = _activeOrderBlocks.Count - 1; i >= 0; i--)
            {
                var ob = _activeOrderBlocks[i];
                bool isMitigated = false;
                if (ob.IsBullish && Bars.LowPrices[index] <= ob.High && Bars.HighPrices[index] >= ob.Low)
                {
                    isMitigated = true;
                }
                else if (!ob.IsBullish && Bars.HighPrices[index] >= ob.Low && Bars.LowPrices[index] <= ob.High)
                {
                    isMitigated = true;
                }

                if (isMitigated)
                {
                    ob.IsMitigated = true;
                    UpdateDrawnOrderBlock(ob, index);
                    _activeOrderBlocks.RemoveAt(i);
                }
            }
        }

        // --- 辅助方法 (现在查找未失效的摇摆点) ---
        private SwingPoint FindLastUnbrokenSwingHighBefore(int index)
        {
            for (int i = _swingHighs.Count - 1; i >= 0; i--)
            {
                if (!_swingHighs[i].IsBroken && _swingHighs[i].Index < index) return _swingHighs[i];
            }
            return null;
        }

        private SwingPoint FindLastUnbrokenSwingLowBefore(int index)
        {
            for (int i = _swingLows.Count - 1; i >= 0; i--)
            {
                if (!_swingLows[i].IsBroken && _swingLows[i].Index < index) return _swingLows[i];
            }
            return null;
        }

        private int FindLastBearishBar(int startIndex, int endIndex)
        {
            for (int i = startIndex; i > endIndex; i--)
            {
                if (Bars.ClosePrices[i] < Bars.OpenPrices[i]) return i;
            }
            return -1;
        }
        
        private int FindLastBullishBar(int startIndex, int endIndex)
        {
            for (int i = startIndex; i > endIndex; i--)
            {
                if (Bars.ClosePrices[i] > Bars.OpenPrices[i]) return i;
            }
            return -1;
        }
        
        // --- 绘图方法 ---
        private void DrawOrderBlock(OrderBlockInfo ob)
        {
            var baseColor = ob.IsBullish ? _bullishColor : _bearishColor;
            // 应用用户自定义的透明度来创建填充颜色
            var finalColor = Color.FromArgb(FillOpacity * 255 / 100, baseColor);

            var name = ob.Name;
            var startTime = Bars.OpenTimes[ob.Index];
            var endTime = Server.Time.AddYears(5);

            ob.Rectangle = Chart.DrawRectangle(name, startTime, ob.High, endTime, ob.Low, finalColor);
            ob.Rectangle.IsFilled = true;

            if (ShowVolumeLabel)
            {
                double volumeRatio = Math.Round(Bars.TickVolumes[ob.Index] / _volumeSma.Result[ob.Index], 1);
                var labelText = $"Vol: {volumeRatio}x";
                var labelY = ob.IsBullish ? ob.Low - Symbol.PipSize * 10 : ob.High + Symbol.PipSize * 10;
                // 标签使用不透明的颜色以保持清晰
                ob.Label = Chart.DrawText(name + "_label", labelText, startTime, labelY, baseColor);
            }
        }

        private void UpdateDrawnOrderBlock(OrderBlockInfo ob, int mitigatedAtIndex)
        {
            if (ob.Rectangle == null) return;
            
            // 应用用户自定义的透明度来创建新的填充颜色
            var finalMitigatedColor = Color.FromArgb(FillOpacity * 255 / 100, _mitigatedColor);

            ob.Rectangle.Color = finalMitigatedColor;

            // **核心修正**: 使用时间周期自适应的逻辑来精确设置结束时间
            TimeSpan barDuration = (mitigatedAtIndex > 0) ? (Bars.OpenTimes[mitigatedAtIndex] - Bars.OpenTimes[mitigatedAtIndex - 1]) : TimeSpan.FromMinutes(1);
            ob.Rectangle.Time2 = Bars.OpenTimes[mitigatedAtIndex] + (barDuration * MitigatedExtensionBars);
            
            if (ob.Label != null)
            {
                ob.Label.Text = "Mitigated";
                ob.Label.Color = _mitigatedColor;
            }
        }
    }
}