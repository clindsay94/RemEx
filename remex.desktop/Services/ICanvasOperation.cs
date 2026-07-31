using System.Collections.ObjectModel;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Services;

/// <summary>A reversible canvas editing operation.</summary>
public interface ICanvasOperation
{
    void Execute();
    void Undo();
}

/// <summary>Records a card position change caused by a drag-drop.</summary>
public sealed class MoveCardOperation : ICanvasOperation
{
    private readonly CanvasCardViewModel _card;
    private readonly double _oldX, _oldY, _newX, _newY;

    public MoveCardOperation(CanvasCardViewModel card, double oldX, double oldY, double newX, double newY)
    {
        _card = card;
        _oldX = oldX; _oldY = oldY;
        _newX = newX; _newY = newY;
    }

    public void Execute() { _card.PositionX = _newX; _card.PositionY = _newY; }
    public void Undo()    { _card.PositionX = _oldX; _card.PositionY = _oldY; }
}

/// <summary>Batches multiple move operations (group-drag).</summary>
public sealed class GroupMoveOperation : ICanvasOperation
{
    private readonly List<MoveCardOperation> _ops;

    public GroupMoveOperation(IEnumerable<MoveCardOperation> ops)
        => _ops = new List<MoveCardOperation>(ops);

    public void Execute() { foreach (var op in _ops) op.Execute(); }
    public void Undo()    { foreach (var op in Enumerable.Reverse(_ops)) op.Undo(); }

    private static class Enumerable
    {
        public static IEnumerable<T> Reverse<T>(List<T> list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
                yield return list[i];
        }
    }
}

/// <summary>Records a card being placed onto the canvas.</summary>
public sealed class AddCardOperation : ICanvasOperation
{
    private readonly ObservableCollection<CanvasCardViewModel> _cards;
    private readonly CanvasCardViewModel _card;

    public AddCardOperation(ObservableCollection<CanvasCardViewModel> cards, CanvasCardViewModel card)
    {
        _cards = cards;
        _card = card;
    }

    public void Execute() { if (!_cards.Contains(_card)) _cards.Add(_card); }
    public void Undo()    { _cards.Remove(_card); }
}

/// <summary>Records a change to a card's pinned-to-home state (reversible group pin/unpin).</summary>
public sealed class PinCardOperation : ICanvasOperation
{
    private readonly CanvasCardViewModel _card;
    private readonly bool _targetPinned;
    private readonly System.Action<CanvasCardViewModel, bool> _apply;

    public PinCardOperation(CanvasCardViewModel card, bool targetPinned,
        System.Action<CanvasCardViewModel, bool> apply)
    {
        _card = card;
        _targetPinned = targetPinned;
        _apply = apply;
    }

    public void Execute() => _apply(_card, _targetPinned);
    public void Undo()    => _apply(_card, !_targetPinned);
}

/// <summary>Batches any set of operations into one undoable unit (group remove / group pin).</summary>
public sealed class CompositeOperation : ICanvasOperation
{
    private readonly List<ICanvasOperation> _ops;

    public CompositeOperation(IEnumerable<ICanvasOperation> ops)
        => _ops = new List<ICanvasOperation>(ops);

    public void Execute() { foreach (var op in _ops) op.Execute(); }
    public void Undo()    { for (int i = _ops.Count - 1; i >= 0; i--) _ops[i].Undo(); }
}

/// <summary>Records a card being removed from the canvas.</summary>
public sealed class RemoveCardOperation : ICanvasOperation
{
    private readonly ObservableCollection<CanvasCardViewModel> _cards;
    private readonly CanvasCardViewModel _card;

    public RemoveCardOperation(ObservableCollection<CanvasCardViewModel> cards, CanvasCardViewModel card)
    {
        _cards = cards;
        _card = card;
    }

    public void Execute() { _cards.Remove(_card); }
    public void Undo()    { if (!_cards.Contains(_card)) _cards.Add(_card); }
}
