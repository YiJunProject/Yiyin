using LightDeck.Core;
namespace LightDeck.App;
internal sealed record TabletRackDesktopLayout(double Width, double Height, double ContentWidth, double ContentHeight, double TrackWidth,
    double InsertHeight, double SendHeight, double OutputHeight, double FaderHeight, double FixedHeight, string Density, double HeaderHeight, RackRowAppearance RowAppearance);