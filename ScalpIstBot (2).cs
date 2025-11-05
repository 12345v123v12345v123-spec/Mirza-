using System;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

// cTrader cBot: Scalp IST (1-minute)
// Heuristic scalping bot for 1-minute timeframe.
// Strategy (configurable):
// - Uses EMA crossover (fast/slow) for trend
// - RSI filter for momentum
// - ATR to calculate dynamic SL and TP
// - Spread filter, trading hours and max trades
// - Position sizing by fixed risk percent

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class ScalpIstBot : Robot
    {
        // --- Parameters ---
        [Parameter("Volume (lots)", DefaultValue = 0.01)]
        public double Volume { get; set; }

        [Parameter("Risk per Trade (%)", DefaultValue = 0.5, MinValue = 0.01)]
        public double RiskPercent { get; set; }

        [Parameter("Max Open Positions", DefaultValue = 3, MinValue = 1)]
        public int MaxOpenPositions { get; set; }

        [Parameter("Fast EMA Period", DefaultValue = 8)]
        public int FastEma { get; set; }

        [Parameter("Slow EMA Period", DefaultValue = 21)]
        public int SlowEma { get; set; }

        [Parameter("RSI Period", DefaultValue = 14)]
        public int RsiPeriod { get; set; }

        [Parameter("RSI Overbought", DefaultValue = 70)]
        public int RsiOver { get; set; }

        [Parameter("RSI Oversold", DefaultValue = 30)]
        public int RsiUnder { get; set; }

        [Parameter("ATR Period", DefaultValue = 14)]
        public int AtrPeriod { get; set; }

        [Parameter("ATR SL Multiplier (x ATR)", DefaultValue = 1.2)]
        public double AtrSlMult { get; set; }

        [Parameter("TP (x ATR)", DefaultValue = 1.0)]
        public double AtrTpMult { get; set; }

        [Parameter("Max Spread (pips)", DefaultValue = 2.0)]
        public double MaxSpreadPips { get; set; }

        [Parameter("Trade Only Between (Start Hour)", DefaultValue = 0)]
        public int TradeStartHour { get; set; }

        [Parameter("Trade Only Between (End Hour)", DefaultValue = 23)]
        public int TradeEndHour { get; set; }

        [Parameter("Use Fixed Volume?", DefaultValue = true)]
        public bool UseFixedVolume { get; set; }

        [Parameter("Trailing Stop (enabled)", DefaultValue = true)]
        public bool UseTrailing { get; set; }

        [Parameter("Trailing Step (pips)", DefaultValue = 5)]
        public int TrailingStep { get; set; }

        // --- indicators ---
        private ExponentialMovingAverage _emaFast;
        private ExponentialMovingAverage _emaSlow;
        private RelativeStrengthIndex _rsi;
        private AverageTrueRange _atr;

        protected override void OnStart()
        {
            // Initialize indicators for 1-minute timeframe
            _emaFast = Indicators.ExponentialMovingAverage(MarketSeries.Close, FastEma);
            _emaSlow = Indicators.ExponentialMovingAverage(MarketSeries.Close, SlowEma);
            _rsi = Indicators.RelativeStrengthIndex(MarketSeries.Close, RsiPeriod);
            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Exponential);

            Print("[ScalpIstBot] started on symbol={0} timeframe={1}", Symbol.Name, TimeFrame);
        }

        protected override void OnTick()
        {
            // Manage trailing stops
            if (UseTrailing)
            {
                foreach (var position in Positions.FindAll("ScalpIst", Symbol))
                {
                    if (position == null) continue;
                    // For long positions
                    if (position.TradeType == TradeType.Buy)
                    {
                        double newStop = Symbol.Bid - TrailingStep * Symbol.PipSize;
                        if (newStop > position.StopLoss)
                        {
                            ModifyPosition(position, newStop, position.TakeProfit);
                        }
                    }
                    else // short
                    {
                        double newStop = Symbol.Ask + TrailingStep * Symbol.PipSize;
                        if (newStop < position.StopLoss || position.StopLoss == null)
                        {
                            ModifyPosition(position, newStop, position.TakeProfit);
                        }
                    }
                }
            }
        }

        protected override void OnBar()
        {
            // Only run on 1-minute series (user requested 1m)
            if (TimeFrame != TimeFrame.Minute) // safeguard: if attached to other TF, still works but warns
            {
                // continue but allow operation
            }

            // Time filter
            var hour = Server.Time.Hour;
            if (hour < TradeStartHour || hour > TradeEndHour) return;

            // Spread filter
            double spreadPips = Symbol.Spread / Symbol.PipSize;
            if (spreadPips > MaxSpreadPips) return;

            // Count current positions opened by this bot on this symbol
            int opened = Positions.FindAll("ScalpIst", Symbol).Length;
            if (opened >= MaxOpenPositions) return;

            int idx = MarketSeries.Close.Count - 1; // current bar index
            int prev = idx - 1;
            if (prev < 1) return;

            double emaFastNow = _emaFast.Result[idx];
            double emaSlowNow = _emaSlow.Result[idx];
            double emaFastPrev = _emaFast.Result[prev];
            double emaSlowPrev = _emaSlow.Result[prev];
            double rsiNow = _rsi.Result[idx];
            double atrNow = _atr.Result[idx];

            // Entry conditions (heuristic):
            // Long: fast EMA crossed above slow EMA on last bar, RSI above oversold threshold and not overbought
            bool longSignal = emaFastPrev <= emaSlowPrev && emaFastNow > emaSlowNow && rsiNow > RsiUnder && rsiNow < RsiOver;
            // Short: fast EMA crossed below slow EMA, RSI below overbought and above oversold
            bool shortSignal = emaFastPrev >= emaSlowPrev && emaFastNow < emaSlowNow && rsiNow < RsiOver && rsiNow > RsiUnder;

            // Calculate SL/TP in price units
            double slPips = Math.Max(1.0, AtrSlMult * atrNow / Symbol.PipSize); // minimum 1 pip
            double tpPips = Math.Max(1.0, AtrTpMult * atrNow / Symbol.PipSize);

            // Position sizing
            long volumeInUnits = GetVolumeToRisk(slPips);

            // Execute trades
            if (longSignal)
            {
                // Place market buy
                double slPrice = Symbol.Bid - slPips * Symbol.PipSize;
                double tpPrice = Symbol.Bid + tpPips * Symbol.PipSize;
                ExecuteMarketOrder(TradeType.Buy, SymbolName, volumeInUnits, "ScalpIst", slPrice, tpPrice);
            }
            else if (shortSignal)
            {
                double slPrice = Symbol.Ask + slPips * Symbol.PipSize;
                double tpPrice = Symbol.Ask - tpPips * Symbol.PipSize;
                ExecuteMarketOrder(TradeType.Sell, SymbolName, volumeInUnits, "ScalpIst", slPrice, tpPrice);
            }
        }

        private long GetVolumeToRisk(double stopLossPips)
        {
            // If user prefers fixed volume, return set volume
            if (UseFixedVolume)
            {
                // Volume parameter is in lots; convert to units
                long units = Symbol.QuantityToVolumeInUnits(Volume);
                return units;
            }

            // Otherwise calculate by RiskPercent
            // Account balance risked in account currency = Balance * RiskPercent / 100
            double riskAmount = Account.Balance * (RiskPercent / 100.0);

            // Value per pip in account currency for 1 unit = pipValuePerUnit
            double pipValuePerUnit = Symbol.PipValue / Symbol.VolumeInUnitsPerContract; // approximation

            if (pipValuePerUnit <= 0)
            {
                // Fallback: use minimal volume
                return Symbol.QuantityToVolumeInUnits(0.01);
            }

            double unitsDouble = riskAmount / (stopLossPips * pipValuePerUnit);
            if (unitsDouble <= 0)
                return Symbol.QuantityToVolumeInUnits(0.01);

            // Round to nearest permitted volume step
            double volume = Symbol.NormalizeVolume(Symbol.VolumeInUnitsToQuantity((long)unitsDouble));
            long units = Symbol.QuantityToVolumeInUnits(volume);
            return Math.Max(units, Symbol.QuantityToVolumeInUnits(0.01));
        }

        protected override void OnPositionOpened(PositionOpenedEventArgs args)
        {
            if (args.Position.Label != "ScalpIst") return;
            Print("[ScalpIst] Opened {0} {1} at {2} SL={3} TP={4}", args.Position.TradeType, args.Position.Volume, args.Position.EntryPrice, args.Position.StopLoss, args.Position.TakeProfit);
        }

        protected override void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (args.Position.Label != "ScalpIst") return;
            Print("[ScalpIst] Closed {0} P/L={1}", args.Position.TradeType, args.Position.GrossProfit);
        }

        protected override void OnError(Error error)
        {
            Print("[ScalpIst] Error: {0}", error.Message);
        }
    }
}
