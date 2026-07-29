namespace Screenshot.App.Editor;

public interface IEditorCommand
{
    void Execute(IList<EditorAnnotation> annotations);

    void Undo(IList<EditorAnnotation> annotations);
}

public sealed class AddAnnotationCommand : IEditorCommand
{
    public AddAnnotationCommand(EditorAnnotation annotation)
    {
        Annotation = annotation;
    }

    public EditorAnnotation Annotation { get; }

    public void Execute(IList<EditorAnnotation> annotations)
    {
        annotations.Add(Annotation);
    }

    public void Undo(IList<EditorAnnotation> annotations)
    {
        _ = annotations.Remove(Annotation);
    }
}
