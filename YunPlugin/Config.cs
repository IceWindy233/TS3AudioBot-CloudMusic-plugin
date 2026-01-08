using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.IO;
using YunPlugin.api;

namespace YunPlugin
{
    public class JSONSerialize
    {
        static public void Serializer<T>(T obj, string path, JsonSerializerSettings config = null)            // 序列化操作
        {
            string json = JsonConvert.SerializeObject(obj, config);
            File.WriteAllText(path, json);
        }

        static public T Deserializer<T>(string path, JsonSerializerSettings config = null)           // 泛型反序列化操作  
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException();
            }

            string json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<T>(json, config);
        }
    }

    public class ApiContainer
    {
        public bool Enable { get; set; } = true;
        [JsonConverter(typeof(StringEnumConverter))]
        public MusicApiType Type { get; set; }
        public string[] Alias { get; set; }
        public MusicApiConfig Config { get; set; }
    }

    public class Config
    {
        public int Version { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public Mode PlayMode { get; set; }
        public bool AutoPause { get; set; }
        [JsonConverter(typeof(StringEnumConverter))]
        public MusicApiType DefaultApi { get; set; }
        public List<ApiContainer> Apis { get; set; }

        [JsonIgnore]
        private string Path { get; set; }
        [JsonIgnore]
        public int CurrentVersion = 1;

        [JsonIgnore]
        private static JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Converters = new List<JsonConverter> { new MusicApiConfigConverter() },
            Formatting = Formatting.Indented
        };

        public ApiContainer GetApiConfig(MusicApiType type)
        {
            foreach (var item in Apis)
            {
                if (item.Type == type)
                {
                    return item;
                }
            }
            return null;
        }

        public ApiContainer GetApiConfig(string alias)
        {
            if (alias == null)
            {
                return null;
            }

            if (Enum.TryParse<MusicApiType>(alias, true, out var type))
            {
                return GetApiConfig(type);
            }

            foreach (var item in Apis)
            {
                if (item.Alias != null)
                {
                    foreach (var a in item.Alias)
                    {
                        if (a.StartsWith(alias))
                        {
                            return item;
                        }
                    }
                }
            }
            return null;
        }

        public static Config GetConfig(string path)
        {
            try
            {
                var config = JSONSerialize.Deserializer<Config>(path, Settings);
                config.Path = path;
                if (config.Version < config.CurrentVersion)
                {
                    config.Version = config.CurrentVersion;
                    config.Save();
                }
                return config;
            }
            catch (FileNotFoundException)
            {
                var config = new Config
                {
                    Version = 1,
                    PlayMode = Mode.SeqPlay,
                    DefaultApi = MusicApiType.Netease,
                    AutoPause = true,
                    Path = path,
                    Apis = new List<ApiContainer>()
                };

                foreach (var item in MusicApiRegister.ApiInterface)
                {
                    var iface = item.Value;
                    if (iface == null)
                    {
                        throw new NotSupportedException($"No MusicApiInterface in MusicApiRegister: {item.Key}");
                    }
                    var tTypes = iface.BaseType.GetGenericArguments();
                    if (tTypes.Length != 1)
                    {
                        throw new NotSupportedException($"No GenericArguments in MusicApiInterface: {iface}");
                    }
                    var tType = tTypes[0];
                    if (tType == null)
                    {
                        throw new NotSupportedException($"No Config Type in MusicApiRegister: {item.Key}");
                    }
                    if (!typeof(MusicApiConfig).IsAssignableFrom(tType))
                    {
                        throw new NotSupportedException($"Config Type is not MusicApiConfig: {tType}");
                    }
                    var apiConfig = (MusicApiConfig)Activator.CreateInstance(tType);
                    apiConfig.SetSaveAction(() => config.Save());
                    config.Apis.Add(new ApiContainer
                    {
                        Type = item.Key,
                        Config = apiConfig
                    });
                }

                config.Save();
                return config;
            }
        }

        public void Save()
        {
            JSONSerialize.Serializer(this, Path, Settings);
        }
    }
}
