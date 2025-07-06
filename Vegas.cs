using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Indicators
{
    [Indicator(IsOverlay = true, TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class Vegas : Indicator
    {
        [Parameter("EMA1周期", DefaultValue = 21)]
        public int EMA1Period { get; set; }

        [Parameter("EMA2周期", DefaultValue = 144)]
        public int EMA2Period { get; set; }

        [Parameter("EMA3周期", DefaultValue = 169)]
        public int EMA3Period { get; set; }

        [Parameter("EMA4周期", DefaultValue = 576)]
        public int EMA4Period { get; set; }

        [Parameter("EMA5周期", DefaultValue = 676)]
        public int EMA5Period { get; set; }

        [Output("EMA1", LineColor = "Blue", Thickness = 1, LineStyle = LineStyle.Solid)]
        public IndicatorDataSeries EMA1 { get; set; }

        [Output("EMA2", LineColor = "Red", Thickness = 1, LineStyle = LineStyle.Solid)]
        public IndicatorDataSeries EMA2 { get; set; }

        [Output("EMA3", LineColor = "Green", Thickness = 1, LineStyle = LineStyle.Solid)]
        public IndicatorDataSeries EMA3 { get; set; }

        [Output("EMA4", LineColor = "Orange", Thickness = 1, LineStyle = LineStyle.Solid)]
        public IndicatorDataSeries EMA4 { get; set; }

        [Output("EMA5", LineColor = "Purple", Thickness = 1, LineStyle = LineStyle.Solid)]
        public IndicatorDataSeries EMA5 { get; set; }

        [Parameter("显示通道", DefaultValue = true, Group = "通道")]
        public bool ShowTunnel { get; set; }

        [Parameter("通道颜色", DefaultValue = "LightGray", Group = "通道")]
        public string TunnelColor { get; set; }

        private readonly List<ExponentialMovingAverage> _emas = new List<ExponentialMovingAverage>();
        private readonly List<IndicatorDataSeries> _emaOutputs = new List<IndicatorDataSeries>();
        private readonly List<int> _emaPeriods = new List<int>();
        
        // 使用线程安全的字典来追踪当前可见的矩形对象，以提高性能
        private readonly ConcurrentDictionary<int, string> _visibleRectangles = new ConcurrentDictionary<int, string>();

        protected override void Initialize()
        {
            _emaPeriods.AddRange(new[] { EMA1Period, EMA2Period, EMA3Period, EMA4Period, EMA5Period });
            _emaOutputs.AddRange(new[] { EMA1, EMA2, EMA3, EMA4, EMA5 });

            for (int i = 0; i < _emaPeriods.Count; i++)
            {
                _emas.Add(Indicators.ExponentialMovingAverage(Bars.ClosePrices, _emaPeriods[i]));
            }

            // 启动一个定时器来处理绘图，避免在Calculate中执行过多操作，从而优化性能
            Timer.Start(TimeSpan.FromSeconds(0.1)); // 每100毫秒触发一次
        }

        public override void Calculate(int index)
        {
            // Calculate方法现在只负责计算指标值，将绘图逻辑分离
            for (int i = 0; i < _emas.Count; i++)
            {
                _emaOutputs[i][index] = _emas[i].Result[index];
            }
        }

        protected override void OnTimer()
        {
            // 如果不显示通道，则清理所有已绘制的对象并返回
            if (!ShowTunnel)
            {
                if (_visibleRectangles.IsEmpty) return;
                
                foreach (var objName in _visibleRectangles.Values)
                {
                    Chart.RemoveObject(objName);
                }
                _visibleRectangles.Clear();
                return;
            }

            int firstVisibleIndex = Chart.FirstVisibleBarIndex;
            int lastVisibleIndex = Chart.LastVisibleBarIndex;

            // 清理不再可见的矩形对象
            var keysToRemove = _visibleRectangles.Keys.Where(k => k < firstVisibleIndex - 1 || k > lastVisibleIndex + 1).ToList();
            foreach (var key in keysToRemove)
            {
                if (_visibleRectangles.TryRemove(key, out var objName))
                {
                    Chart.RemoveObject(objName);
                }
            }

            // 仅绘制可见区域的矩形
            for (int i = firstVisibleIndex; i <= lastVisibleIndex; i++)
            {
                if (i < 1 || _visibleRectangles.ContainsKey(i)) continue;

                var objectName = "VegasTunnel_" + i;
                var top = Math.Max(EMA2[i], EMA3[i]);
                var bottom = Math.Min(EMA2[i], EMA3[i]);
                var color = Color.FromName(TunnelColor);
                color = Color.FromArgb(70, color);

                // 健壮地处理最后一根K线的时间，通过前一根K线估算时间间隔来避免越界错误
                var time2 = (i < Bars.Count - 1) ? Bars.OpenTimes[i + 1] : Bars.OpenTimes[i] + (Bars.OpenTimes[i] - Bars.OpenTimes[i - 1]);

                Chart.DrawRectangle(objectName, Bars.OpenTimes[i], top, time2, bottom, color, 1, LineStyle.Solid).IsFilled = true;
                _visibleRectangles.TryAdd(i, objectName);
            }
        }
    
        // 确保指标停止时清理资源
        protected override void OnDestroy()
        {
            Timer.Stop();
            // 确保在指标实例被销毁时，所有创建的图表对象都被移除
            if (_visibleRectangles == null) return;
            foreach (var objName in _visibleRectangles.Values)
            {
                Chart.RemoveObject(objName);
            }
            _visibleRectangles.Clear();
            base.OnDestroy();
        }
    }
}