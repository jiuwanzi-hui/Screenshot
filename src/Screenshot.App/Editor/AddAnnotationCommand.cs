namespace Screenshot.App.Editor;

public sealed class AddAnnotationCommand
{
    public AddAnnotationCommand(EditorAnnotation annotation)
    {
        Annotation = annotation;
    }

    public EditorAnnotation Annotation { get; }

    public void Execute(ICollection<EditorAnnotation> annotations)
    {
        annotations.Add(Annotation);
    }

    public void Undo(IList<EditorAnnotation> annotations)
    {
        _ = annotations.Remove(Annotation);
    }
}
