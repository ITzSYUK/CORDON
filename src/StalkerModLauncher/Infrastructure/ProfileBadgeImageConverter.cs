using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using StalkerModLauncher.Models;

namespace StalkerModLauncher.Infrastructure;

public sealed class ProfileBadgeImageConverter : IValueConverter
{
    private readonly Dictionary<ProfileBadge, BitmapImage> _images = [];

    internal BitmapImage? GetImage(ProfileBadge badge)
    {
        if (badge == ProfileBadge.Default)
        {
            return null;
        }

        if (!_images.TryGetValue(badge, out var image))
        {
            var fileName = badge.ToString().ToLowerInvariant();
            image = new BitmapImage(new Uri($"pack://application:,,,/CORDON;component/Resources/ProfileBadges/{fileName}.png"));
            image.Freeze();
            _images.Add(badge, image);
        }

        return image;
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ProfileBadge badge ? GetImage(badge) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
