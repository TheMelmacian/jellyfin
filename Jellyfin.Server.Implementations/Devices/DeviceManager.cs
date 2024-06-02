using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Entities.Security;
using Jellyfin.Data.Enums;
using Jellyfin.Data.Events;
using Jellyfin.Data.Queries;
using Jellyfin.Extensions;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Devices;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Server.Implementations.Devices
{
    /// <summary>
    /// Manages the creation, updating, and retrieval of devices.
    /// </summary>
    public class DeviceManager : IDeviceManager
    {
        private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;
        private readonly IUserManager _userManager;
        private readonly ConcurrentDictionary<string, ClientCapabilities> _capabilitiesMap = new();
        private readonly ConcurrentDictionary<int, Device> _devices;
        private readonly ConcurrentDictionary<string, DeviceOptions> _deviceOptions;

        /// <summary>
        /// Initializes a new instance of the <see cref="DeviceManager"/> class.
        /// </summary>
        /// <param name="dbProvider">The database provider.</param>
        /// <param name="userManager">The user manager.</param>
        public DeviceManager(IDbContextFactory<JellyfinDbContext> dbProvider, IUserManager userManager)
        {
            _dbProvider = dbProvider;
            _userManager = userManager;
            _devices = new ConcurrentDictionary<int, Device>();
            _deviceOptions = new ConcurrentDictionary<string, DeviceOptions>();

            using var dbContext = _dbProvider.CreateDbContext();
            foreach (var device in dbContext.Devices
                         .Include(d => d.User)
                         .OrderBy(d => d.Id)
                         .AsEnumerable())
            {
                _devices.TryAdd(device.Id, device);
            }

            foreach (var deviceOption in dbContext.DeviceOptions
                         .OrderBy(d => d.Id)
                         .AsEnumerable())
            {
                _deviceOptions.TryAdd(deviceOption.DeviceId, deviceOption);
            }
        }

        /// <inheritdoc />
        public event EventHandler<GenericEventArgs<Tuple<string, DeviceOptions>>>? DeviceOptionsUpdated;

        /// <inheritdoc />
        public void SaveCapabilities(string deviceId, ClientCapabilities capabilities)
        {
            _capabilitiesMap[deviceId] = capabilities;
        }

        /// <inheritdoc />
        public async Task UpdateDeviceOptions(string deviceId, string deviceName)
        {
            DeviceOptions? deviceOptions;
            var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
            await using (dbContext.ConfigureAwait(false))
            {
                deviceOptions = await dbContext.DeviceOptions.FirstOrDefaultAsync(dev => dev.DeviceId == deviceId).ConfigureAwait(false);
                if (deviceOptions is null)
                {
                    deviceOptions = new DeviceOptions(deviceId);
                    dbContext.DeviceOptions.Add(deviceOptions);
                }

                deviceOptions.CustomName = deviceName;
                await dbContext.SaveChangesAsync().ConfigureAwait(false);
            }

            _deviceOptions[deviceId] = deviceOptions;

            DeviceOptionsUpdated?.Invoke(this, new GenericEventArgs<Tuple<string, DeviceOptions>>(new Tuple<string, DeviceOptions>(deviceId, deviceOptions)));
        }

        /// <inheritdoc />
        public async Task<Device> CreateDevice(Device device)
        {
            var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
            await using (dbContext.ConfigureAwait(false))
            {
                dbContext.Devices.Add(device);

                await dbContext.SaveChangesAsync().ConfigureAwait(false);
                var newDevice = await dbContext.Devices
                    .Include(d => d.User)
                    .FirstOrDefaultAsync(d => d.Id == device.Id)
                    .ConfigureAwait(false);
                _devices.TryAdd(device.Id, newDevice!);
            }

            return device;
        }

        /// <inheritdoc />
        public DeviceOptions GetDeviceOptions(string deviceId)
        {
            _deviceOptions.TryGetValue(deviceId, out var deviceOptions);

            return deviceOptions ?? new DeviceOptions(deviceId);
        }

        /// <inheritdoc />
        public ClientCapabilities GetCapabilities(string deviceId)
        {
            return _capabilitiesMap.TryGetValue(deviceId, out ClientCapabilities? result)
                ? result
                : new ClientCapabilities();
        }

        /// <inheritdoc />
        public DeviceInfo? GetDevice(string id)
        {
            var device = _devices.Values.OrderByDescending(d => d.DateLastActivity).FirstOrDefault(d => d.DeviceId == id);
            _deviceOptions.TryGetValue(id, out var deviceOption);

            var deviceInfo = device is null ? null : ToDeviceInfo(device, deviceOption);
            return deviceInfo;
        }

        /// <inheritdoc />
        public QueryResult<Device> GetDevices(DeviceQuery query)
        {
            var devices = _devices.Values.OrderBy(d => d.Id)
                .Where(device => !query.UserId.HasValue || device.UserId.Equals(query.UserId.Value))
                .Where(device => query.DeviceId == null || device.DeviceId == query.DeviceId)
                .Where(device => query.AccessToken == null || device.AccessToken == query.AccessToken);
            var count = devices.Count();

            if (query.Skip.HasValue)
            {
                devices = devices.Skip(query.Skip.Value);
            }

            if (query.Limit.HasValue)
            {
                devices = devices.Take(query.Limit.Value);
            }

            return new QueryResult<Device>(query.Skip, count, devices.ToList());
        }

        /// <inheritdoc />
        public QueryResult<DeviceInfo> GetDeviceInfos(DeviceQuery query)
        {
            var devices = GetDevices(query);

            return new QueryResult<DeviceInfo>(
                devices.StartIndex,
                devices.TotalRecordCount,
                devices.Items.Select(device => ToDeviceInfo(device)).ToList());
        }

        /// <inheritdoc />
        public QueryResult<DeviceInfo> GetDevicesForUser(Guid? userId)
        {
            IEnumerable<Device> devices = _devices.Values
                .OrderByDescending(d => d.DateLastActivity)
                .ThenBy(d => d.DeviceId);

            if (!userId.IsNullOrEmpty())
            {
                var user = _userManager.GetUserById(userId.Value);
                if (user is null)
                {
                    throw new ResourceNotFoundException();
                }

                devices = devices.Where(i => CanAccessDevice(user, i.DeviceId));
            }

            var array = devices.Select(device =>
                {
                    _deviceOptions.TryGetValue(device.DeviceId, out var option);
                    return ToDeviceInfo(device, option);
                }).ToArray();

            return new QueryResult<DeviceInfo>(array);
        }

        /// <inheritdoc />
        public async Task DeleteDevice(Device device)
        {
            _devices.TryRemove(device.Id, out _);
            var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
            await using (dbContext.ConfigureAwait(false))
            {
                dbContext.Devices.Remove(device);
                await dbContext.SaveChangesAsync().ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public async Task UpdateDevice(Device device)
        {
            var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
            await using (dbContext.ConfigureAwait(false))
            {
                dbContext.Devices.Update(device);
                await dbContext.SaveChangesAsync().ConfigureAwait(false);
            }

            _devices[device.Id] = device;
        }

        /// <inheritdoc />
        public bool CanAccessDevice(User user, string deviceId)
        {
            ArgumentNullException.ThrowIfNull(user);
            ArgumentException.ThrowIfNullOrEmpty(deviceId);

            if (user.HasPermission(PermissionKind.EnableAllDevices) || user.HasPermission(PermissionKind.IsAdministrator))
            {
                return true;
            }

            return user.GetPreference(PreferenceKind.EnabledDevices).Contains(deviceId, StringComparison.OrdinalIgnoreCase)
                   || !GetCapabilities(deviceId).SupportsPersistentIdentifier;
        }

        private DeviceInfo ToDeviceInfo(Device authInfo, DeviceOptions? options = null)
        {
            var caps = GetCapabilities(authInfo.DeviceId);

            return new DeviceInfo
            {
                AppName = authInfo.AppName,
                AppVersion = authInfo.AppVersion,
                Id = authInfo.DeviceId,
                LastUserId = authInfo.UserId,
                LastUserName = authInfo.User.Username,
                Name = authInfo.DeviceName,
                DateLastActivity = authInfo.DateLastActivity,
                IconUrl = caps.IconUrl,
                CustomName = options?.CustomName,
            };
        }
    }
}
