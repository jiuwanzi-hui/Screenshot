namespace Screenshot.App.Text;

internal static class OcrReadingOrder
{
    internal static OcrTextRegion[] Sort(IEnumerable<OcrTextRegion> regions)
    {
        var rows = new List<List<OcrTextRegion>>();
        foreach (var region in regions.OrderBy(r => r.Y).ThenBy(r => r.X))
        {
            // Tiny detector Y differences must not scramble widely spaced
            // characters on one line. Tall columns retain their own order.
            var row = region.Height <= region.Width * 1.8
                ? rows.FirstOrDefault(items => items.All(other => other.Height <= other.Width * 1.8 &&
                    Math.Min(region.Y + region.Height, other.Y + other.Height) - Math.Max(region.Y, other.Y) >=
                    Math.Min(region.Height, other.Height) * .5))
                : rows.FirstOrDefault(items => items.All(other => other.Height > other.Width * 1.8 &&
                    Math.Abs(region.Y - other.Y) <= Math.Min(region.Width, other.Width) * .5));
            if (row is null) rows.Add([region]);
            else row.Add(region);
        }
        return rows.SelectMany(row => row.OrderBy(r => r.X)).ToArray();
    }
}
