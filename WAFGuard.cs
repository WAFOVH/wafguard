using Oxide.Core;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WAFGuard", "WAF.OVH", "1.5.1")]
    public class WAFGuard : RustPlugin
    {
        #region Configuration
        private Configuration config;
        
        protected override void LoadDefaultConfig()
        {
            Config.Clear();
            Config["MaxSpeedHack"] = 7.5f;
            Config["MaxFlyHeight"] = 10f;
            Config["MaxResourceGatherRate"] = 100;
            Config["KickOnViolation"] = true;
            Config["ViolationsBeforeBan"] = 3;
            Config["LogViolations"] = true;
            Config["WallhackCheckInterval"] = 0.5f;
            Config["MaxWallTraceDistance"] = 100f;
            Config["SuspiciousWallTracesBeforeViolation"] = 5;
            Config["SuspiciousAimSnapThreshold"] = 120f;
            Config["AimSnapCheckInterval"] = 0.1f;
            Config["RequiredSnapDetections"] = 3;
            Config["WallTraceMemoryDuration"] = 30f;
        }

        void Init()
        {
            LoadConfig();
            config = new Configuration
            {
                MaxSpeedHack = Config.Get<float>("MaxSpeedHack"),
                MaxFlyHeight = Config.Get<float>("MaxFlyHeight"),
                MaxResourceGatherRate = Config.Get<int>("MaxResourceGatherRate"),
                KickOnViolation = Config.Get<bool>("KickOnViolation"),
                ViolationsBeforeBan = Config.Get<int>("ViolationsBeforeBan"),
                LogViolations = Config.Get<bool>("LogViolations"),
                WallhackCheckInterval = Config.Get<float>("WallhackCheckInterval"),
                MaxWallTraceDistance = Config.Get<float>("MaxWallTraceDistance"),
                SuspiciousWallTracesBeforeViolation = Config.Get<int>("SuspiciousWallTracesBeforeViolation"),
                SuspiciousAimSnapThreshold = Config.Get<float>("SuspiciousAimSnapThreshold"),
                AimSnapCheckInterval = Config.Get<float>("AimSnapCheckInterval"),
                RequiredSnapDetections = Config.Get<int>("RequiredSnapDetections"),
                WallTraceMemoryDuration = Config.Get<float>("WallTraceMemoryDuration")
            };
        }

        public class Configuration
        {
            public float MaxSpeedHack { get; set; }
            public float MaxFlyHeight { get; set; }
            public int MaxResourceGatherRate { get; set; }
            public bool KickOnViolation { get; set; }
            public int ViolationsBeforeBan { get; set; }
            public bool LogViolations { get; set; }
            public float WallhackCheckInterval { get; set; }
            public float MaxWallTraceDistance { get; set; }
            public int SuspiciousWallTracesBeforeViolation { get; set; }
            public float SuspiciousAimSnapThreshold { get; set; }
            public float AimSnapCheckInterval { get; set; }
            public int RequiredSnapDetections { get; set; }
            public float WallTraceMemoryDuration { get; set; }
        }
        #endregion

        #region Data Storage
        private Dictionary<ulong, PlayerViolationData> playerViolations = new Dictionary<ulong, PlayerViolationData>();

        class PlayerViolationData
        {
            public int ViolationCount { get; set; } = 0;
            public DateTime LastViolation { get; set; }
            public List<string> ViolationHistory { get; set; } = new List<string>();
            public int SuspiciousWallTraces { get; set; } = 0;
            public DateTime LastWallTraceReset { get; set; } = DateTime.Now;
            public Vector3 LastAimDirection { get; set; }
            public float LastAimTime { get; set; }
            public int AimSnapDetections { get; set; }
            public Dictionary<BasePlayer, float> WallTraceTargets { get; set; } = new Dictionary<BasePlayer, float>();
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

            if (!playerViolations.ContainsKey(player.userID))
            {
                playerViolations[player.userID] = new PlayerViolationData();
            }

            CheckSpeedHack(player);
            CheckFlyHack(player);
            CheckWallhack(player);
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
            float currentSpeed = player.GetNetworkPosition().magnitude;
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

        private void CheckWallhack(BasePlayer player)
        {
            if (player.IsAdmin || player.IsSleeping()) return;

            if (!playerViolations.ContainsKey(player.userID))
            {
                playerViolations[player.userID] = new PlayerViolationData();
            }

            CheckWallTrace(player);
            CheckAimSnap(player);
            CheckTargetConsistency(player);
            CleanupOldTraces(player);
        }

        private void CheckWallTrace(BasePlayer player)
        {
            if (player.IsAdmin || player.IsSleeping()) return;

            if ((DateTime.Now - playerViolations[player.userID].LastWallTraceReset).TotalSeconds > 30)
            {
                playerViolations[player.userID].SuspiciousWallTraces = 0;
                playerViolations[player.userID].LastWallTraceReset = DateTime.Now;
            }

            RaycastHit hit;
            Ray ray = new Ray(player.eyes.position, player.eyes.HeadForward());
            
            if (Physics.Raycast(ray, out hit, config.MaxWallTraceDistance, LayerMask.GetMask("Player_Server")))
            {
                BasePlayer target = hit.GetEntity() as BasePlayer;
                if (target != null && target != player)
                {
                    RaycastHit wallHit;
                    if (Physics.Raycast(player.eyes.position, (target.transform.position - player.eyes.position).normalized, 
                        out wallHit, Vector3.Distance(player.eyes.position, target.transform.position), 
                        LayerMask.GetMask("Construction", "World", "Terrain")))
                    {
                        playerViolations[player.userID].SuspiciousWallTraces++;
                        
                        if (playerViolations[player.userID].SuspiciousWallTraces >= config.SuspiciousWallTracesBeforeViolation)
                        {
                            OnPlayerViolation(player, $"Possible wallhack detected: Tracking players through walls");
                            playerViolations[player.userID].SuspiciousWallTraces = 0;
                        }
                    }
                }
            }
        }

        private void CheckAimSnap(BasePlayer player)
        {
            var data = playerViolations[player.userID];
            float currentTime = Time.realtimeSinceStartup;
            
            if (currentTime - data.LastAimTime < config.AimSnapCheckInterval)
                return;

            Vector3 currentAim = player.eyes.HeadForward();
            
            if (data.LastAimDirection != Vector3.zero)
            {
                float angleChange = Vector3.Angle(data.LastAimDirection, currentAim);
                
                if (angleChange > config.SuspiciousAimSnapThreshold)
                {
                    data.AimSnapDetections++;
                    
                    if (data.AimSnapDetections >= config.RequiredSnapDetections)
                    {
                        OnPlayerViolation(player, $"Possible aimhack detected: Suspicious aim snapping ({angleChange:F1}°)");
                        data.AimSnapDetections = 0;
                    }
                }
            }
            
            data.LastAimDirection = currentAim;
            data.LastAimTime = currentTime;
        }

        private void CheckTargetConsistency(BasePlayer player)
        {
            var data = playerViolations[player.userID];
            float currentTime = Time.realtimeSinceStartup;
            
            RaycastHit hit;
            Ray ray = new Ray(player.eyes.position, player.eyes.HeadForward());
            
            if (Physics.Raycast(ray, out hit, config.MaxWallTraceDistance, LayerMask.GetMask("Player_Server")))
            {
                BasePlayer target = hit.GetEntity() as BasePlayer;
                if (target != null && target != player)
                {
                    if (!data.WallTraceTargets.ContainsKey(target))
                    {
                        data.WallTraceTargets[target] = currentTime;
                    }
                    else if (currentTime - data.WallTraceTargets[target] > 5f)
                    {
                        OnPlayerViolation(player, $"Possible wallhack detected: Consistent tracking through walls");
                        data.WallTraceTargets.Remove(target);
                    }
                }
            }
        }

        private void CleanupOldTraces(BasePlayer player)
        {
            var data = playerViolations[player.userID];
            float currentTime = Time.realtimeSinceStartup;
            
            List<BasePlayer> playersToRemove = new List<BasePlayer>();
            foreach (var kvp in data.WallTraceTargets)
            {
                if (currentTime - kvp.Value > config.WallTraceMemoryDuration)
                {
                    playersToRemove.Add(kvp.Key);
                }
            }
            
            foreach (var playerToRemove in playersToRemove)
            {
                data.WallTraceTargets.Remove(playerToRemove);
            }
        }
        #endregion

        #region Punishment Methods
        private void KickPlayer(BasePlayer player, string reason)
        {
            player.Kick("WAFGuard: " + reason);
        }

        private void BanPlayer(BasePlayer player)
        {
            player.IPlayer.Ban("WAFGuard: Multiple Violations Detected");
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
                    ShowPlayerViolations(targetPlayer, player);
                }
            }
            else
            {
                ShowOverallStats(player);
            }
        }

        [ConsoleCommand("waf.status")]
        private void ConsoleCheckStatus(ConsoleSystem.Arg arg)
        {
            if (arg.IsAdmin)
            {
                if (arg.Args?.Length > 0)
                {
                    BasePlayer targetPlayer = BasePlayer.Find(arg.Args[0]);
                    if (targetPlayer != null)
                    {
                        ShowPlayerViolations(targetPlayer, arg);
                    }
                }
                else
                {
                    ShowOverallStats(arg);
                }
            }
        }

        [ConsoleCommand("waf.reset")]
        private void ResetPlayerViolations(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            
            if (arg.Args?.Length > 0)
            {
                BasePlayer targetPlayer = BasePlayer.Find(arg.Args[0]);
                if (targetPlayer != null)
                {
                    playerViolations.Remove(targetPlayer.userID);
                    arg.ReplyWith($"Reset violations for player {targetPlayer.displayName}");
                }
            }
        }

        [ChatCommand("wafhelp")]
        private void ChatHelp(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin) return;
            
            ShowHelpMessage(player);
        }

        [ConsoleCommand("waf.help")]
        private void ConsoleHelp(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            
            ShowHelpMessage(arg);
        }

        private void ShowHelpMessage(object output)
        {
            string[] messages = {
                "WAFGuard Commands:",
                "Chat Commands:",
                "/wafstatus [player] - Show violation statistics",
                "/wafhelp - Show this help message",
                "",
                "Console Commands:",
                "waf.status [player] - Show violation statistics",
                "waf.reset [player] - Reset violations for a player",
                "waf.help - Show this help message"
            };

            if (output is BasePlayer)
            {
                foreach (var message in messages)
                {
                    SendReply(output as BasePlayer, message);
                }
            }
            else if (output is ConsoleSystem.Arg)
            {
                (output as ConsoleSystem.Arg).ReplyWith(string.Join("\n", messages));
            }
        }

        private void ShowPlayerViolations(BasePlayer target, object output)
        {
            if (playerViolations.ContainsKey(target.userID))
            {
                var data = playerViolations[target.userID];
                string[] messages = {
                    $"WAFGuard Violations for {target.displayName}:",
                    $"Total violations: {data.ViolationCount}",
                    $"Last violation: {data.LastViolation}"
                };

                foreach (var message in messages)
                {
                    if (output is BasePlayer)
                        SendReply(output as BasePlayer, message);
                    else if (output is ConsoleSystem.Arg)
                        (output as ConsoleSystem.Arg).ReplyWith(message);
                }

                foreach (var violation in data.ViolationHistory)
                {
                    if (output is BasePlayer)
                        SendReply(output as BasePlayer, violation);
                    else if (output is ConsoleSystem.Arg)
                        (output as ConsoleSystem.Arg).ReplyWith(violation);
                }
            }
            else
            {
                string message = $"No violations recorded for {target.displayName}";
                if (output is BasePlayer)
                    SendReply(output as BasePlayer, message);
                else if (output is ConsoleSystem.Arg)
                    (output as ConsoleSystem.Arg).ReplyWith(message);
            }
        }

        private void ShowOverallStats(object output)
        {
            int totalViolations = 0;
            foreach (var data in playerViolations.Values)
            {
                totalViolations += data.ViolationCount;
            }

            string[] messages = {
                "WAFGuard Statistics:",
                $"Total players monitored: {playerViolations.Count}",
                $"Total violations detected: {totalViolations}"
            };

            foreach (var message in messages)
            {
                if (output is BasePlayer)
                    SendReply(output as BasePlayer, message);
                else if (output is ConsoleSystem.Arg)
                    (output as ConsoleSystem.Arg).ReplyWith(message);
            }
        }
        #endregion

        private object OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || input == null) return null;

            if (float.IsNaN(input.current.aimAngles.x) || float.IsNaN(input.current.aimAngles.y) ||
                float.IsInfinity(input.current.aimAngles.x) || float.IsInfinity(input.current.aimAngles.y))
            {
                OnPlayerViolation(player, "Invalid input detected: NaN/Infinity in aim angles");
                return false;
            }

            if (((int)input.current.buttons & (int)(BUTTON.FORWARD | BUTTON.BACKWARD | BUTTON.LEFT | BUTTON.RIGHT)) > 0)
            {
                Vector3 movement = player.GetNetworkPosition() - player.transform.position;
                if (movement.magnitude > 1.5f)
                {
                    OnPlayerViolation(player, "Invalid input detected: Movement values out of range");
                    return false;
                }
            }

            return null;
        }

        #region Combat Checks
        private void CheckProjectileHack(BasePlayer attacker, HitInfo info, Vector3 startPos)
        {
            if (info?.HitEntity == null || !(info.HitEntity is BasePlayer)) return;
            BasePlayer victim = info.HitEntity as BasePlayer;

            BasePlayer.FiredProjectile firedProjectile;
            if (!attacker.firedProjectiles.TryGetValue(info.ProjectileID, out firedProjectile)) return;

            float desyncTime = attacker.desyncTimeClamped;
            float clientFrames = 2f / 60f;
            float serverFrames = 2f * Mathx.Max(Time.deltaTime, Time.smoothDeltaTime, Time.fixedDeltaTime);
            float forgiveness = 1.5f;
            float maxTime = (desyncTime + clientFrames + serverFrames) * forgiveness;

            var originShoot = attacker.FindBone("head").position;
            var eyeDist = Vector3.Distance(originShoot, startPos);
            var maxEyeDist = config.WallTraceMemoryDuration + 3f + (maxTime * 0.1f);

            if (eyeDist > maxEyeDist && !attacker.GetMounted())
            {
                OnPlayerViolation(attacker, $"Projectile desync detected: {eyeDist:F2}m > {maxEyeDist:F2}m");
                return;
            }

            if (victim != null)
            {
                var nextProjPos = startPos + info.ProjectileVelocity * 0.005f;
                var realDirection = startPos + attacker.eyes.BodyForward() * (info.ProjectileVelocity * 0.005f).magnitude;
                var pointDist = Vector3.Distance(realDirection, nextProjPos);
                var maxAngle = config.WallTraceMemoryDuration + maxTime * 0.1f;

                if (pointDist > maxAngle)
                {
                    OnPlayerViolation(attacker, $"Invalid projectile angle: {pointDist:F2}m > {maxAngle:F2}m");
                    return;
                }
            }
        }

        private void CheckMeleeHack(BasePlayer attacker, HitInfo info)
        {
            if (info?.HitEntity == null || !(info.HitEntity is BasePlayer)) return;
            BasePlayer victim = info.HitEntity as BasePlayer;

            var startPos = attacker.eyes.position;
            var weapon = info.WeaponPrefab as BaseMelee;
            if (weapon == null) return;

            var maxDistance = weapon.maxDistance + weapon.attackRadius;
            var hitPos = info.HitPositionWorld;
            var originDist = Vector3.Distance(startPos, hitPos);
            
            float desyncTime = attacker.desyncTimeClamped;
            float clientFrames = ConVar.AntiHack.melee_clientframes / 60f;
            float serverFrames = ConVar.AntiHack.melee_serverframes * Mathx.Max(Time.deltaTime, Time.smoothDeltaTime, Time.fixedDeltaTime);
            float maxTime = (desyncTime + clientFrames + serverFrames) * 1.5f;
            
            var maxOriginDist = config.WallTraceMemoryDuration + 1.2f + maxTime * 0.5f;
            var nearFlag = originDist <= maxOriginDist;
            var realDirection = startPos + attacker.eyes.BodyForward() * (nearFlag ? maxDistance - 1f : maxDistance);
            var pointDist = Vector3.Distance(realDirection, hitPos);
            var maxAngle = config.WallTraceMemoryDuration + (victim.IsCrawling() || victim.IsSleeping() || victim.IsWounded() ? 1f : 0f) 
                + (maxDistance < 1.5f ? maxDistance : maxDistance - clientFrames + 0.2f) + maxTime * 0.5f;

            if (pointDist > maxAngle)
            {
                OnPlayerViolation(attacker, $"Invalid melee attack angle: {pointDist:F2}m > {maxAngle:F2}m");
            }
        }
        #endregion

        private object OnPlayerAttack(BasePlayer attacker, HitInfo info)
        {
            if (info?.Weapon is BaseProjectile)
            {
                CheckProjectileHack(attacker, info, attacker.eyes.position);
            }
            return null;
        }

        private object OnMeleeAttack(BasePlayer attacker, HitInfo info)
        {
            CheckMeleeHack(attacker, info);
            return null;
        }
    }
}
