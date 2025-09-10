namespace SpheneShared.Utils.Configuration;

public interface ISpheneConfiguration
{
    T GetValueOrDefault<T>(string key, T defaultValue);
    T GetValue<T>(string key);
    string SerializeValue(string key, string defaultValue);
}
