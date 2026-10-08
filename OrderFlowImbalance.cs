using System;
using System.Drawing;
using System.Threading;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Chart;

namespace QuantowerIndicators
{
    /// <summary>
    /// Book pressure from the Binance book Quantower already streams.
    /// No second connection. Each NewLevel2 snapshot is compared with the
    /// previous one.
    ///
    /// Bid grew or ask shrank: buyers. Ask grew or bid shrank: sellers.
    /// </summary>
    public class OrderFlowImbalance : Indicator
    {
        private const int MaxLevels = 5;

        [InputParameter("Prices to watch", 10, 1, 5, 1, 0)]
        public int Levels = 1;

        private readonly double[] bidPrice = new double[MaxLevels];
        private readonly double[] bidSize = new double[MaxLevels];
        private readonly double[] askPrice = new double[MaxLevels];
        private readonly double[] askSize = new double[MaxLevels];
        private readonly double[] prevBidPrice = new double[MaxLevels];
        private readonly double[] prevBidSize = new double[MaxLevels];
        private readonly double[] prevAskPrice = new double[MaxLevels];
        private readonly double[] prevAskSize = new double[MaxLevels];

        private int prevCount;
        private bool hasPrevious;
        private double thisBar;
        private double runningTotal;
        private long barTime;
        private long lastRedraw;
        private bool historyCleared;
        private volatile int alive;
        private int busy;
        private double tick;
        private string status = "Waiting for the Binance book";
        private readonly object gate = new object();

        public OrderFlowImbalance()
        {
            Name = "Book pressure";
            Description = "Buyers and sellers from the Quantower Binance book.";
            SeparateWindow = true;

            AddLineSeries("Buyers", Color.LimeGreen, 3, LineStyle.Histogramm);
            AddLineSeries("Sellers", Color.OrangeRed, 3, LineStyle.Histogramm);
            AddLineSeries("Running total", Color.DodgerBlue, 1, LineStyle.Solid);
            AddLineSeries("Zero", Color.DimGray, 1, LineStyle.Solid);
        }

        protected override void OnInit()
        {
            hasPrevious = false;
            prevCount = 0;
            thisBar = 0;
            runningTotal = 0;
            barTime = 0;
            lastRedraw = 0;
            historyCleared = false;
            alive = 1;
            busy = 0;
            status = "Waiting for the Binance book";
            tick = Symbol != null && Symbol.TickSize > 0 ? Symbol.TickSize : 0;

            if (Symbol == null)
                return;

            Symbol.NewLevel2 -= OnBook;
            Symbol.NewLevel2 += OnBook;
        }

        protected override void OnClear()
        {
            alive = 0;
            if (Symbol != null)
                Symbol.NewLevel2 -= OnBook;
        }

        // Quantower raises this from the Binance depth feed already connected.
        private void OnBook(Symbol symbol, Level2Quote quote, DOMQuote book)
        {
            if (alive == 0 || book == null || book.Bids == null || book.Asks == null)
                return;
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
                return;

            try
            {
                int levels = Math.Max(1, Math.Min(Levels, MaxLevels));
                int n = ReadBook(book, levels);
                if (n == 0)
                {
                    lock (gate)
                    {
                        hasPrevious = false;
                        status = "Book skipped";
                    }
                    return;
                }

                lock (gate)
                {
                    if (hasPrevious && prevCount == n)
                    {
                        double pressure = CompareBook(n);
                        if (!double.IsNaN(pressure) && !double.IsInfinity(pressure))
                            thisBar += pressure;
                    }

                    for (int i = 0; i < n; i++)
                    {
                        prevBidPrice[i] = bidPrice[i];
                        prevBidSize[i] = bidSize[i];
                        prevAskPrice[i] = askPrice[i];
                        prevAskSize[i] = askSize[i];
                    }
                    prevCount = n;
                    hasPrevious = true;
                    status = thisBar > 0 ? "Buyers adding" : thisBar < 0 ? "Sellers adding" : "Balanced";
                }
            }
            catch
            {
                lock (gate)
                {
                    hasPrevious = false;
                    status = "Book skipped";
                }
            }
            finally
            {
                Interlocked.Exchange(ref busy, 0);
            }

            long now = DateTime.UtcNow.Ticks;
            if (now - Interlocked.Read(ref lastRedraw) < TimeSpan.TicksPerMillisecond * 200)
                return;
            Interlocked.Exchange(ref lastRedraw, now);

            try
            {
                if (CurrentChart != null)
                    CurrentChart.RedrawBuffer();
            }
            catch
            {
            }
        }

        // Best bid is the highest price, best ask the lowest.
        // Quantower's list is usually already sorted. This still picks the
        // best if a snapshot is not.
        private int ReadBook(DOMQuote book, int levels)
        {
            int bids = TakeBest(book.Bids, bidPrice, bidSize, levels, true);
            int asks = TakeBest(book.Asks, askPrice, askSize, levels, false);
            int n = Math.Min(bids, asks);
            if (n == 0 || askPrice[0] <= bidPrice[0])
                return 0;
            return n;
        }

        private int TakeBest(System.Collections.Generic.IList<Level2Quote> rows, double[] prices, double[] sizes, int levels, bool bids)
        {
            int n = 0;
            int limit = Math.Min(rows.Count, 40);
            for (int i = 0; i < limit; i++)
            {
                Level2Quote row = rows[i];
                if (row == null || row.Closed || row.Size <= 0 || double.IsNaN(row.Price) || double.IsInfinity(row.Price) || double.IsNaN(row.Size))
                    continue;

                int same = -1;
                for (int j = 0; j < n; j++)
                {
                    if (Math.Abs(row.Price - prices[j]) <= (tick > 0 ? tick * 0.5 : 1e-8))
                    {
                        same = j;
                        break;
                    }
                }
                if (same >= 0)
                {
                    sizes[same] += row.Size;
                    continue;
                }

                int place = n;
                for (int j = 0; j < n; j++)
                {
                    bool better = bids ? row.Price > prices[j] : row.Price < prices[j];
                    if (!better)
                        continue;
                    place = j;
                    break;
                }

                if (place == n && n == levels)
                    continue;

                for (int j = Math.Min(n, levels - 1); j > place; j--)
                {
                    prices[j] = prices[j - 1];
                    sizes[j] = sizes[j - 1];
                }
                prices[place] = row.Price;
                sizes[place] = row.Size;
                if (n < levels)
                    n++;
            }
            return n;
        }

        private double CompareBook(int n)
        {
            double pressure = 0;
            for (int i = 0; i < n; i++)
            {
                pressure += Side(prevBidPrice[i], prevBidSize[i], bidPrice[i], bidSize[i], true);
                pressure += Side(prevAskPrice[i], prevAskSize[i], askPrice[i], askSize[i], false);
            }
            return pressure;
        }

        private double Side(double oldPrice, double oldSize, double newPrice, double newSize, bool bid)
        {
            if (SamePrice(oldPrice, newPrice))
                return bid ? newSize - oldSize : oldSize - newSize;

            if (bid)
                return newPrice > oldPrice ? newSize : -oldSize;

            return newPrice < oldPrice ? -newSize : oldSize;
        }

        private bool SamePrice(double a, double b)
        {
            double tolerance = tick > 0 ? tick * 0.5 : Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b))) * 1e-8;
            return Math.Abs(a - b) <= tolerance;
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            if (alive == 0 || args.Reason == UpdateReason.HistoricalBar || Count < 1)
                return;

            if (!historyCleared)
            {
                for (int i = Count - 1; i >= 1; i--)
                {
                    SetValue(0, 0, i);
                    SetValue(0, 1, i);
                    SetValue(double.NaN, 2, i);
                    SetValue(0, 3, i);
                }
                historyCleared = true;
            }

            long ticks;
            try
            {
                ticks = Time().Ticks;
            }
            catch
            {
                return;
            }

            double closed = double.NaN;
            double bar;
            double total;

            lock (gate)
            {
                if (barTime != 0 && ticks != barTime)
                {
                    closed = thisBar;
                    runningTotal += thisBar;
                    thisBar = 0;
                    status = "Balanced";
                }

                barTime = ticks;
                bar = thisBar;
                total = runningTotal + thisBar;
            }

            try
            {
                if (!double.IsNaN(closed) && Count > 1)
                    Draw(closed, runningTotal, 1);
                Draw(bar, total, 0);
            }
            catch
            {
            }
        }

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            if (alive == 0 || args == null || args.Graphics == null)
                return;

            string text;
            lock (gate)
            {
                text = status
                    + "    this bar " + thisBar.ToString("0")
                    + "    running total " + (runningTotal + thisBar).ToString("0");
            }

            try
            {
                args.Graphics.DrawString(text, SystemFonts.DefaultFont, Brushes.Gainsboro, 8, 6);
            }
            catch
            {
            }
        }

        private void Draw(double bar, double total, int offset)
        {
            SetValue(bar > 0 ? bar : 0, 0, offset);
            SetValue(bar < 0 ? bar : 0, 1, offset);
            SetValue(total, 2, offset);
            SetValue(0, 3, offset);
        }
    }
}
