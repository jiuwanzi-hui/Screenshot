namespace Screenshot.App.Editor;

public sealed class EditorDocument
{
    private readonly List<EditorAnnotation> _annotations = [];
    private readonly Stack<AddAnnotationCommand> _undoStack = [];
    private readonly Stack<AddAnnotationCommand> _redoStack = [];

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
}
