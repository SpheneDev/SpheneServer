using SpheneShared.Utils.Configuration;

namespace SpheneShared.Services;

public interface IConfigurationService<T> where T : class, ISpheneConfiguration
{
    bool IsMain { get; }

    event EventHandler ConfigChangedEvent;

    T1 GetValue<T1>(string key);
    T1 GetValueOrDefault<T1>(string key, T1 defaultValue);
    string ToString();
}
