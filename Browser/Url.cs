using System;

namespace Browser
{
    public sealed class Url
    {
        public string Value { get; }
        public string Protocol { get; }
        public string Host { get; }
        public string Path { get; }

        public Url(string url)
        {
            Value = url;
            var uri = new Uri(url);
            Protocol = uri.Scheme;
            Host = uri.Host;
            Path = uri.AbsolutePath;
        }

        public static bool TryCreate(string url, out Url? result)
        {
            result = null;
            try
            {
                if (string.IsNullOrWhiteSpace(url))
                    return false;

                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    return false;

                result = new Url(uri.ToString());
                return true;
            }
            catch
            {
                return false;
            }
        }

        public override string ToString() => Value;
    }
}