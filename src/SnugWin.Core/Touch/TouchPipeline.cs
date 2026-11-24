using System;
using System.Collections.Generic;
using System.Linq;
using SnugWin.Core.Support;

namespace SnugWin.Core.Touch;

public sealed record TouchStep(bool PassOriginal, IReadOnlyList<TouchAction> Actions)
{
    public static readonly TouchStep Pass = new(true, Array.Empty<TouchAction>());

    public bool Equals(TouchStep? other) =>
        other is not null
        && PassOriginal == other.PassOriginal
        && Actions.SequenceEqual(other.Actions);

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(PassOriginal);
        foreach (var action in Actions)
            hc.Add(action);
        return hc.ToHashCode();
    }
}

public abstract record TouchAction
{
    public sealed record HoldOriginal : TouchAction;
    public sealed record Perform(TouchCommand Command, TouchRegion? Region) : TouchAction;
    public sealed record DropHeld : TouchAction;
}

public sealed class TouchPipeline
{
    private readonly IReadOnlyList<Func<ITouchNormalizer>> _factories;
    private ITouchNormalizer[] _normalizers;
    private TouchTranslator _translator;
    private TouchRegion? _pressed;

    public TouchPipeline(IReadOnlyList<Func<ITouchNormalizer>>? normalizerFactories = null)
    {
        _factories = normalizerFactories is { Count: > 0 }
            ? normalizerFactories.ToArray()
            : [static () => new DirectTouch()];
        _normalizers = CreateNormalizers(_factories);
    }

    public void Reset()
    {
        _normalizers = CreateNormalizers(_factories);
        _translator = default;
        _pressed = null;
    }

    public static IReadOnlySet<PointerEventKind> KindsOf(IEnumerable<ITouchNormalizer> normalizers)
    {
        var set = new HashSet<PointerEventKind>();
        foreach (var n in normalizers)
            set.UnionWith(n.Consumes);
        return set;
    }

    public bool IsCoasting => _translator.IsCoasting;

    public ScrollStep? MomentumFrame() => _translator.MomentumFrame();

    public double? Deadline
    {
        get
        {
            double? min = null;
            foreach (var n in _normalizers)
            {
                if (n.Deadline is { } d && (min is null || d < min.Value))
                    min = d;
            }
            return min;
        }
    }

    public TouchStep Take(PointerEvent ev, int source, IReadOnlyList<TouchRegion> regions, TouchOptions? options = null)
    {
        if (source < 0 || source >= _normalizers.Length)
            return TouchStep.Pass;

        var normalizer = _normalizers[source];
        if (!normalizer.Consumes.Contains(ev.Kind))
            return TouchStep.Pass;

        var opts = options ?? TouchOptions.Default;
        var actions = new List<TouchAction>();

        if (normalizer.Deadline is { } due && due <= ev.Time)
            Feed(normalizer.Tick(ev.Time), actions, regions, opts);

        var normalized = normalizer.Normalize(ev);
        Feed(normalized.Before, actions, regions, opts);

        if (normalized.Current is not { } current)
            return actions.Count == 0 ? TouchStep.Pass : new TouchStep(true, actions);

        var response = Translate(current, regions, opts);
        if (current.Kind == TouchInputKind.Press && !response.PassOriginal)
            actions.Add(new TouchAction.HoldOriginal());
        foreach (var cmd in response.Commands)
            actions.Add(new TouchAction.Perform(cmd, _pressed));
        if (current.Kind == TouchInputKind.Release)
            actions.Add(new TouchAction.DropHeld());

        return response.PassOriginal && actions.Count == 0
            ? TouchStep.Pass
            : new TouchStep(response.PassOriginal, actions);
    }

    public TouchStep Tick(double time, IReadOnlyList<TouchRegion> regions, TouchOptions? options = null)
    {
        var opts = options ?? TouchOptions.Default;
        var actions = new List<TouchAction>();
        foreach (var normalizer in _normalizers)
        {
            if (normalizer.Deadline is { } due && due <= time)
                Feed(normalizer.Tick(time), actions, regions, opts);
        }
        return actions.Count == 0 ? TouchStep.Pass : new TouchStep(true, actions);
    }

    private TouchResponse Translate(TouchInput input, IReadOnlyList<TouchRegion> regions, TouchOptions options)
    {
        if (input.Kind == TouchInputKind.Press)
            _pressed = regions.FirstOrDefault(r => r.Frame.Contains(input.Location));
        return _translator.Handle(input, _pressed, options);
    }

    private void Feed(IReadOnlyList<TouchInput> inputs, List<TouchAction> actions, IReadOnlyList<TouchRegion> regions, TouchOptions options)
    {
        foreach (var input in inputs)
        {
            foreach (var cmd in Translate(input, regions, options).Commands)
            {
                if (cmd is TouchCommand.ReplayPress or TouchCommand.ReplayCurrent)
                {
                    Log.Error($"手势整理补出来的输入（{input.Kind}）要补发原事件（{cmd}），可它没有原事件：跳过。整理交出的输入不合语法");
                    continue;
                }
                actions.Add(new TouchAction.Perform(cmd, _pressed));
            }
        }
    }

    private static ITouchNormalizer[] CreateNormalizers(IReadOnlyList<Func<ITouchNormalizer>> factories)
    {
        var list = new ITouchNormalizer[factories.Count];
        for (int i = 0; i < factories.Count; i++)
            list[i] = factories[i]();
        return list;
    }
}
