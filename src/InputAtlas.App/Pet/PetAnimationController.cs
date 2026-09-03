using InputAtlas.Core;

namespace InputAtlas.App.Pet;

public sealed class PetAnimationController
{
    public static readonly TimeSpan MinimumActionDuration = TimeSpan.FromMilliseconds(90);

    private readonly HashSet<InputId> _leftKeys = [];
    private readonly HashSet<InputId> _rightKeys = [];
    private readonly HashSet<InputId> _bothKeys = [];
    private readonly HashSet<PetInputAction> _mouseButtons = [];
    private DateTimeOffset _stateEnteredAt;
    private bool _paused;

    public PetAnimationController(DateTimeOffset now)
    {
        _stateEnteredAt = now;
    }

    public PetVisualState CurrentState { get; private set; } = PetVisualState.Idle;

    public DateTimeOffset? NextTransitionAt { get; private set; }

    public (bool StateChanged, bool PulseRequested) ApplyInput(
        InputId input,
        bool isPressed,
        DateTimeOffset now)
    {
        var action = PetInputMapper.Map(input);
        var changed = action switch
        {
            PetInputAction.KeyboardLeft => UpdateSet(_leftKeys, input, isPressed),
            PetInputAction.KeyboardRight => UpdateSet(_rightKeys, input, isPressed),
            PetInputAction.KeyboardBoth => UpdateSet(_bothKeys, input, isPressed),
            PetInputAction.MouseLeft or
            PetInputAction.MouseRight or
            PetInputAction.MouseMiddle or
            PetInputAction.MouseBack or
            PetInputAction.MouseForward => UpdateSet(_mouseButtons, action, isPressed),
            _ => false,
        };

        var stateChanged = changed && Evaluate(now);
        var pulseRequested = changed && isPressed && !_paused;
        return (stateChanged, pulseRequested);
    }

    public bool SetPaused(bool paused, DateTimeOffset now)
    {
        if (_paused == paused)
        {
            return false;
        }

        _paused = paused;
        ResetPressedSets();
        return TransitionTo(paused ? PetVisualState.Paused : PetVisualState.Idle, now);
    }

    public bool Advance(DateTimeOffset now) => Evaluate(now);

    private bool Evaluate(DateTimeOffset now)
    {
        var requested = DetermineRequestedState();
        if (requested == PetVisualState.Idle &&
            CurrentState is not PetVisualState.Idle and not PetVisualState.Paused)
        {
            var earliestIdle = _stateEnteredAt + MinimumActionDuration;
            if (now < earliestIdle)
            {
                NextTransitionAt = earliestIdle;
                return false;
            }
        }

        return TransitionTo(requested, now);
    }

    private PetVisualState DetermineRequestedState()
    {
        if (_paused)
        {
            return PetVisualState.Paused;
        }

        if (_mouseButtons.Count > 0)
        {
            return MouseStateByPriority();
        }

        var left = _leftKeys.Count > 0 || _bothKeys.Count > 0;
        var right = _rightKeys.Count > 0 || _bothKeys.Count > 0;
        return (left, right) switch
        {
            (true, true) => PetVisualState.KeyboardBoth,
            (true, false) => PetVisualState.KeyboardLeft,
            (false, true) => PetVisualState.KeyboardRight,
            _ => PetVisualState.Idle,
        };
    }

    private PetVisualState MouseStateByPriority()
    {
        if (_mouseButtons.Contains(PetInputAction.MouseLeft))
        {
            return PetVisualState.MouseLeft;
        }

        if (_mouseButtons.Contains(PetInputAction.MouseRight))
        {
            return PetVisualState.MouseRight;
        }

        if (_mouseButtons.Contains(PetInputAction.MouseMiddle))
        {
            return PetVisualState.MouseMiddle;
        }

        if (_mouseButtons.Contains(PetInputAction.MouseBack))
        {
            return PetVisualState.MouseBack;
        }

        return PetVisualState.MouseForward;
    }

    private bool TransitionTo(PetVisualState state, DateTimeOffset now)
    {
        NextTransitionAt = null;
        if (CurrentState == state)
        {
            return false;
        }

        CurrentState = state;
        _stateEnteredAt = now;
        return true;
    }

    private void ResetPressedSets()
    {
        _leftKeys.Clear();
        _rightKeys.Clear();
        _bothKeys.Clear();
        _mouseButtons.Clear();
        NextTransitionAt = null;
    }

    private static bool UpdateSet<T>(HashSet<T> set, T value, bool isPressed) where T : notnull =>
        isPressed ? set.Add(value) : set.Remove(value);
}
