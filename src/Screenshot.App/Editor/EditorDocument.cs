namespace Screenshot.App.Editor;

public sealed class EditorDocument
{
    private readonly List<EditorAnnotation> _annotations = [];
    private readonly Stack<IEditorCommand> _undoStack = [];
    private readonly Stack<IEditorCommand> _redoStack = [];

    public IReadOnlyList<EditorAnnotation> Annotations => _annotations;

    public bool CanUndo => _undoStack.Count > 0;

    public bool CanRedo => _redoStack.Count > 0;

    public void Add(EditorAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);

        var command = new AddAnnotationCommand(annotation);
        command.Execute(_annotations);
        _undoStack.Push(command);
        _redoStack.Clear();
    }

    public void SetAt(int index, EditorAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if ((uint)index >= (uint)_annotations.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _annotations[index] = annotation;
    }

    public void ReplaceAt(
        int index,
        EditorAnnotation previous,
        EditorAnnotation replacement)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(replacement);
        if ((uint)index >= (uint)_annotations.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _annotations[index] = replacement;
        _undoStack.Push(new ReplaceAnnotationCommand(
            index,
            previous,
            replacement));
        _redoStack.Clear();
    }

    public void RemoveAt(int index)
    {
        if ((uint)index >= (uint)_annotations.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var command = new RemoveAnnotationCommand(index, _annotations[index]);
        command.Execute(_annotations);
        _undoStack.Push(command);
        _redoStack.Clear();
    }

    public void Undo()
    {
        if (_undoStack.TryPop(out var command))
        {
            command.Undo(_annotations);
            _redoStack.Push(command);
        }
    }

    public void Redo()
    {
        if (_redoStack.TryPop(out var command))
        {
            command.Execute(_annotations);
            _undoStack.Push(command);
        }
    }

    public void TransformAnnotations(
        Func<EditorAnnotation, EditorAnnotation> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);

        for (var index = 0; index < _annotations.Count; index++)
        {
            _annotations[index] = transform(_annotations[index]);
        }

        _undoStack.Clear();
        foreach (var annotation in _annotations)
        {
            _undoStack.Push(new AddAnnotationCommand(annotation));
        }

        _redoStack.Clear();
    }
}

file sealed class RemoveAnnotationCommand(
    int index,
    EditorAnnotation annotation) : IEditorCommand
{
    public void Execute(IList<EditorAnnotation> annotations)
    {
        annotations.RemoveAt(index);
    }

    public void Undo(IList<EditorAnnotation> annotations)
    {
        annotations.Insert(index, annotation);
    }
}

file sealed class ReplaceAnnotationCommand(
    int index,
    EditorAnnotation previous,
    EditorAnnotation replacement) : IEditorCommand
{
    public void Execute(IList<EditorAnnotation> annotations)
    {
        annotations[index] = replacement;
    }

    public void Undo(IList<EditorAnnotation> annotations)
    {
        annotations[index] = previous;
    }
}
