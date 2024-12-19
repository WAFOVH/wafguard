using Oxide.Core;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WAFGuard", "WAF.OVH", "1.0.0")]
    public class WAFGuard : RustPlugin
    {
        #region Configuration
        private Configuration config;
        
        class Configuration
        {
            public float MaxSpeedHack = 7.5f;
            public float MaxFlyHeight = 10f;
            public int MaxResourceGatherRate = 100;
            public bool KickOnViolation = true;
            public int ViolationsBeforeBan = 3;
            public bool LogViolations = true;
        }

        protected override void LoadConfig()
        {
            config = Config.ReadObject<Configuration>();
            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            config = new Configuration();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(config);
        }
        #endregion

        #region Data Storage
        private Dictionary<ulong, PlayerViolationData> playerViolations = new Dictionary<ulong, PlayerViolationData>();

        class PlayerViolationData
        {
            public int ViolationCount { get; set; } = 0;
            public DateTime LastViolation { get; set; }
            public List<string> ViolationHistory { get; set; } = new List<string>();
        }
        #endregion

        #region Oxide Hooks
        private void OnPlayerViolation(BasePlayer player, string violation)
        {
            if (player == null) return;

            if (!playerViolations.ContainsKey(player.userID))
            {
                playerViolations[player.userID] = new PlayerViolationData();
            }

            var data = playerViolations[player.userID];
            data.ViolationCount++;
            data.LastViolation = DateTime.Now;
            data.ViolationHistory.Add($"{DateTime.Now}: {violation}");

            if (config.LogViolations)
            {
                LogToFile("wafguard", $"{player.displayName} ({player.userID}) - {violation}", this);
            }

            if (data.ViolationCount >= config.ViolationsBeforeBan)
            {
                BanPlayer(player);
            }
            else if (config.KickOnViolation)
            {
                KickPlayer(player, violation);
            }
        }

        void OnPlayerTick(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;

            CheckSpeedHack(player);
            CheckFlyHack(player);
        }

        void OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item)
        {
            if (item.amount > config.MaxResourceGatherRate)
            {
                BasePlayer player = entity.ToPlayer();
                if (player != null)
                {
                    OnPlayerViolation(player, $"Resource hack detected: {item.amount} {item.info.shortname}");
                    item.amount = config.MaxResourceGatherRate;
                }
            }
        }
        #endregion

        #region Check Methods
        private void CheckSpeedHack(BasePlayer player)
        {
            float currentSpeed = player.GetMoveSpeed();
            if (currentSpeed > config.MaxSpeedHack && !player.IsAdmin)
            {
                OnPlayerViolation(player, $"Speed hack detected: {currentSpeed:F2}");
            }
        }

        private void CheckFlyHack(BasePlayer player)
        {
            if (!player.IsAdmin && !player.isMounted)
            {
                float heightAboveGround = GetHeightAboveGround(player);
                if (heightAboveGround > config.MaxFlyHeight)
                {
                    OnPlayerViolation(player, $"Fly hack detected: {heightAboveGround:F2}m above ground");
                }
            }
        }

        private float GetHeightAboveGround(BasePlayer player)
        {
            RaycastHit hit;
            if (Physics.Raycast(player.transform.position, Vector3.down, out hit, 1000f, LayerMask.GetMask("Terrain", "World")))
            {
                return hit.distance;
            }
            return 0f;
        }
        #endregion

        #region Punishment Methods
        private void KickPlayer(BasePlayer player, string reason)
        {
            player.Kick("WAFGuard: " + reason);
        }

        private void BanPlayer(BasePlayer player)
        {
            Server.Ban(player.UserIDString, "WAFGuard: Multiple Violations Detected");
        }
        #endregion

        #region Commands
        [ChatCommand("wafstatus")]
        private void CheckStatus(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin) return;

            if (args.Length > 0)
            {
                BasePlayer targetPlayer = BasePlayer.Find(args[0]);
                if (targetPlayer != null)
                {
                    ShowPlayerViolations(player, targetPlayer);
                }
            }
            else
            {
                ShowOverallStats(player);
            }
        }

        private void ShowPlayerViolations(BasePlayer admin, BasePlayer target)
        {
            if (playerViolations.ContainsKey(target.userID))
            {
                var data = playerViolations[target.userID];
                SendReply(admin, $"WAFGuard Violations for {target.displayName}:");
                SendReply(admin, $"Total violations: {data.ViolationCount}");
                SendReply(admin, $"Last violation: {data.LastViolation}");
                foreach (var violation in data.ViolationHistory)
                {
                    SendReply(admin, violation);
                }
            }
            else
            {
                SendReply(admin, $"No violations recorded for {target.displayName}");
            }
        }

        private void ShowOverallStats(BasePlayer admin)
        {
            SendReply(admin, "WAFGuard Statistics:");
            SendReply(admin, $"Total players monitored: {playerViolations.Count}");
            int totalViolations = 0;
            foreach (var data in playerViolations.Values)
            {
                totalViolations += data.ViolationCount;
            }
            SendReply(admin, $"Total violations detected: {totalViolations}");
        }
        #endregion
    }
}
