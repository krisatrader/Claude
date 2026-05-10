using System;
using cAlgo.API;
using Python.Runtime;

namespace cAlgo.Robots;

[Robot(AccessRights = AccessRights.None, AddIndicators = true)]
public partial class LondonGoldStrategy : Robot
{
    [Parameter("EMA Period", DefaultValue = 21, Group = "Indicators")]
    public int EmaPeriod { get; set; }

    [Parameter("RSI Period", DefaultValue = 14, Group = "Indicators")]
    public int RsiPeriod { get; set; }

    [Parameter("ATR Period", DefaultValue = 14, Group = "Indicators")]
    public int AtrPeriod { get; set; }

    [Parameter("Risk Percent", DefaultValue = 1.0, Group = "Risk Management")]
    public double RiskPercent { get; set; }

    [Parameter("ATR Multiplier (SL)", DefaultValue = 1.5, Group = "Risk Management")]
    public double AtrMultiplier { get; set; }

    [Parameter("Reward Ratio", DefaultValue = 2.0, Group = "Risk Management")]
    public double RewardRatio { get; set; }

    [Parameter("London Open UTC", DefaultValue = 7, Group = "Time Window")]
    public int LondonOpenUtc { get; set; }

    [Parameter("London Close UTC", DefaultValue = 10, Group = "Time Window")]
    public int LondonCloseUtc { get; set; }
}