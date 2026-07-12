using CounterStrikeSharp.API.Modules.Utils;
using RetakesAllocatorCore.Config;

namespace RetakesAllocatorCore.Managers;

public class RoundTypeManager
{
    #region Instance management

    private static RoundTypeManager? _instance;

    public static RoundTypeManager Instance => _instance ??= new RoundTypeManager();

    #endregion

    private string? _map;
    
    private RoundType? _nextRoundTypeOverride;
    private RoundType? _currentRoundType;
    private CsTeam? _forceBuyTeam;
    private DateTime? _roundLiveTimeUtc;

    private RoundTypeSelectionOption _roundTypeSelection;
    private readonly List<RoundType> _roundsOrder = new();
    private int _roundTypeManualOrderingPosition;

    private RoundTypeManager()
    {
        Initialize();
    }

    public void Initialize()
    {
        _nextRoundTypeOverride = null;
        _currentRoundType = null;
        _forceBuyTeam = null;
        _roundLiveTimeUtc = null;
        _roundTypeSelection = Configs.GetConfigData().RoundTypeSelection;

        _roundsOrder.Clear();
        switch (_roundTypeSelection)
        {
            case RoundTypeSelectionOption.RandomFixedCounts:
                foreach (var (roundType, fixedCount) in Configs.GetConfigData().RoundTypeRandomFixedCounts)
                {
                    for (var i = 0; i < fixedCount; i++)
                    {
                        _roundsOrder.Add(roundType);
                    }
                }
                Utils.Shuffle(_roundsOrder);
                break;
            case RoundTypeSelectionOption.ManualOrdering:
                foreach (var item in Configs.GetConfigData().RoundTypeManualOrdering)
                {
                    for (var i = 0; i < item.Count; i++)
                    {
                        _roundsOrder.Add(item.Type);
                    }
                }
                break;
        }
        _roundTypeManualOrderingPosition = 0;
    }

    public void SetMap(string map)
    {
        _map = map;
    }

    public string? Map => _map;

    public RoundType GetNextRoundType()
    {
        if (_nextRoundTypeOverride is not null)
        {
            return _nextRoundTypeOverride.Value;
        }

        switch (_roundTypeSelection)
        {
            case RoundTypeSelectionOption.Random:
                return GetRandomRoundType();
            case RoundTypeSelectionOption.ManualOrdering:
            case RoundTypeSelectionOption.RandomFixedCounts:
                return GetNextRoundTypeInOrder();
        }

        throw new Exception("No round type selection type was found.");
    }

    private RoundType GetNextRoundTypeInOrder()
    {
        if (_roundTypeManualOrderingPosition >= _roundsOrder.Count)
        {
            _roundTypeManualOrderingPosition = 0;
        }
        return _roundsOrder[_roundTypeManualOrderingPosition++];
    }

    private RoundType GetRandomRoundType()
    {
        var randomValue = new Random().NextDouble();

        var pistolPercentage = Configs.GetConfigData().GetRoundTypePercentage(RoundType.Pistol);

        if (randomValue < pistolPercentage)
        {
            return RoundType.Pistol;
        }

        if (randomValue < Configs.GetConfigData().GetRoundTypePercentage(RoundType.HalfBuy) + pistolPercentage)
        {
            return RoundType.HalfBuy;
        }

        return RoundType.FullBuy;
    }

    public void SetNextRoundTypeOverride(RoundType? nextRoundType)
    {
        _nextRoundTypeOverride = nextRoundType;
    }

    public RoundType? GetCurrentRoundType()
    {
        return _currentRoundType;
    }

    public void SetCurrentRoundType(RoundType? currentRoundType)
    {
        _currentRoundType = currentRoundType;
    }

    #region ForceBuy (one-sided half buy) handling

    /// <summary>
    /// On a HalfBuy ("Force Buy") round, the team that is force-buying.
    /// The other team gets a full buy. Null on any other round type.
    /// </summary>
    public CsTeam? ForceBuyTeam => _forceBuyTeam;

    private CsTeam? _nextForceBuyTeamOverride;

    /// <summary>
    /// Forces which team force-buys on the next force-buy round (eg. driven by
    /// Competitive Retakes' loss-streak rule). One-shot.
    /// </summary>
    public void SetNextForceBuyTeamOverride(CsTeam? team)
    {
        _nextForceBuyTeamOverride = team;
    }

    public CsTeam? ConsumeForceBuyTeamOverride()
    {
        var value = _nextForceBuyTeamOverride;
        _nextForceBuyTeamOverride = null;
        return value;
    }

    public void SetForceBuyTeam(CsTeam? team)
    {
        _forceBuyTeam = team;
    }

    /// <summary>
    /// The round type a given team actually plays. On a force-buy round the
    /// non-force-buying team effectively plays a FullBuy round.
    /// </summary>
    public RoundType GetEffectiveRoundType(RoundType roundType, CsTeam team)
    {
        if (
            roundType == RoundType.HalfBuy &&
            _forceBuyTeam is not null &&
            team is CsTeam.Terrorist or CsTeam.CounterTerrorist &&
            team != _forceBuyTeam
        )
        {
            return RoundType.FullBuy;
        }

        return roundType;
    }

    public RoundType? GetCurrentEffectiveRoundType(CsTeam team)
    {
        return _currentRoundType is null ? null : GetEffectiveRoundType(_currentRoundType.Value, team);
    }

    #endregion

    #region Weapon change window

    /// <summary>
    /// Called when freeze time ends and the round goes live.
    /// </summary>
    public void SetRoundLive()
    {
        _roundLiveTimeUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Called at round-start allocation, before freeze time ends.
    /// </summary>
    public void ResetRoundLiveTime()
    {
        _roundLiveTimeUtc = null;
    }

    /// <summary>
    /// True while weapon changes may still be applied immediately: either the
    /// round is not live yet (freeze time), or fewer than windowSeconds have
    /// passed since it went live.
    /// </summary>
    public bool IsInWeaponChangeWindow(double windowSeconds)
    {
        if (_roundLiveTimeUtc is null)
        {
            return true;
        }

        return (DateTime.UtcNow - _roundLiveTimeUtc.Value).TotalSeconds <= windowSeconds;
    }

    #endregion
}
