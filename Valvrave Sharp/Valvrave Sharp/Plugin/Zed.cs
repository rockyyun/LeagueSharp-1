﻿namespace Valvrave_Sharp.Plugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Windows.Forms;

    using LeagueSharp;
    using LeagueSharp.SDK.Core;
    using LeagueSharp.SDK.Core.Enumerations;
    using LeagueSharp.SDK.Core.Extensions;
    using LeagueSharp.SDK.Core.Extensions.SharpDX;
    using LeagueSharp.SDK.Core.UI.IMenu.Values;
    using LeagueSharp.SDK.Core.Utils;
    using LeagueSharp.SDK.Core.Wrappers;

    using SharpDX;

    using Valvrave_Sharp.Core;

    using Color = System.Drawing.Color;
    using Menu = LeagueSharp.SDK.Core.UI.IMenu.Menu;

    internal class Zed : Program
    {
        #region Constants

        private const float OverkillValue = 1.2f;

        #endregion

        #region Static Fields

        private static Obj_GeneralParticleEmitter deathMark;

        private static bool eForced;

        private static bool wCasted, rCasted;

        private static Obj_AI_Minion wShadow, rShadow;

        #endregion

        #region Constructors and Destructors

        public Zed()
        {
            Q = new Spell(SpellSlot.Q, 925);
            Q2 = new Spell(SpellSlot.Q, 925);
            W = new Spell(SpellSlot.W, 550);
            E = new Spell(SpellSlot.E, 290);
            R = new Spell(SpellSlot.R, 625);
            Q.SetSkillshot(0.25f, 50, 1700, true, SkillshotType.SkillshotLine);
            Q2.SetSkillshot(0.25f, 50, 1700, true, SkillshotType.SkillshotLine);
            E.SetTargetted(0.05f, float.MaxValue);
            Q.DamageType = Q2.DamageType = E.DamageType = R.DamageType = DamageType.Physical;
            Q.MinHitChance = Q2.MinHitChance = HitChance.VeryHigh;

            var orbwalkMenu = new Menu("Orbwalk", "Orbwalk");
            {
                Config.Separator(orbwalkMenu, "blank0", "Q/E/Ignite/Item: Always On");
                Config.Separator(orbwalkMenu, "blank1", "W Settings");
                Config.Bool(orbwalkMenu, "WNormal", "Use For Non-R Combo");
                Config.List(orbwalkMenu, "WAdv", "Use For R Combo", new[] { "OFF", "Line", "Triangle", "Mouse" });
                Config.List(orbwalkMenu, "WSwapGap", "Swap To Gap Close", new[] { "OFF", "Smart", "Always" });
                Config.Separator(orbwalkMenu, "blank2", "R Settings");
                Config.Bool(orbwalkMenu, "R", "Use R");
                Config.Slider(
                    orbwalkMenu,
                    "RStopRange",
                    "Priorize If Ready And Distance <=",
                    (int)(R.Range + 225),
                    (int)R.Range,
                    (int)(R.Range + W.Range));
                Config.List(orbwalkMenu, "RSwapGap", "Swap To Gap Close", new[] { "OFF", "Smart", "Always" });
                Config.Slider(orbwalkMenu, "RSwapIfHpU", "Swap If Hp < (%)", 20);
                Config.Bool(orbwalkMenu, "RSwapIfKill", "Swap If Mark Can Kill Target");
                Config.Separator(orbwalkMenu, "blank3", "Extra R Settings");
                foreach (var enemy in GameObjects.EnemyHeroes)
                {
                    Config.Bool(orbwalkMenu, "RCast" + enemy.ChampionName, "Cast On " + enemy.ChampionName, false);
                }
                MainMenu.Add(orbwalkMenu);
            }
            var hybridMenu = new Menu("Hybrid", "Hybrid");
            {
                Config.List(hybridMenu, "Mode", "Mode", new[] { "W-E-Q", "E-Q", "Q" });
                Config.Bool(hybridMenu, "QOverW", "Priorize Q Over W", false);
                Config.Separator(hybridMenu, "blank4", "Auto Q Settings");
                Config.KeyBind(hybridMenu, "AutoQ", "KeyBind", Keys.T, KeyBindType.Toggle);
                Config.Slider(hybridMenu, "AutoQMpA", "If Mp >=", 100, 0, 200);
                MainMenu.Add(hybridMenu);
            }
            var lhMenu = new Menu("LastHit", "Last Hit");
            {
                Config.Bool(lhMenu, "Q", "Use Q");
                Config.Bool(lhMenu, "E", "Use E", false);
                MainMenu.Add(lhMenu);
            }
            var ksMenu = new Menu("KillSteal", "Kill Steal");
            {
                Config.Bool(ksMenu, "Q", "Use Q");
                Config.Bool(ksMenu, "E", "Use E");
                MainMenu.Add(ksMenu);
            }
            var drawMenu = new Menu("Draw", "Draw");
            {
                Config.Bool(drawMenu, "Q", "Q Range");
                Config.Bool(drawMenu, "W", "W Range");
                Config.Bool(drawMenu, "E", "E Range", false);
                Config.Bool(drawMenu, "R", "R Range");
                Config.Bool(drawMenu, "Target", "Target");
                Config.Bool(drawMenu, "WPos", "W Shadow");
                Config.Bool(drawMenu, "RPos", "R Shadow");
                MainMenu.Add(drawMenu);
            }
            Config.KeyBind(MainMenu, "FleeW", "Use W To Flee", Keys.C);

            Game.OnUpdate += OnUpdate;
            Drawing.OnDraw += OnDraw;
            Obj_AI_Base.OnProcessSpellCast += OnProcessSpellCast;
            GameObject.OnCreate += OnCreate;
            GameObject.OnDelete += OnDelete;
        }

        #endregion

        #region Properties

        private static Obj_AI_Hero GetTarget
        {
            get
            {
                float range = 0;
                if (wShadow.IsValid() && rShadow.IsValid())
                {
                    range += Math.Max(Player.Distance(rShadow), Player.Distance(wShadow));
                }
                else if (WState == 0 && rShadow.IsValid())
                {
                    range += Math.Max(Player.Distance(rShadow), W.Range);
                }
                else if (wShadow.IsValid())
                {
                    range += Player.Distance(wShadow);
                }
                else if (WState == 0)
                {
                    range += W.Range;
                }
                var target = Q.GetTarget(range);
                if (RState == 0 && MainMenu["Orbwalk"]["R"].GetValue<MenuBool>().Value)
                {
                    var targets =
                        GameObjects.EnemyHeroes.Where(
                            i =>
                            i.IsValidTarget(Q.Range + range)
                            && MainMenu["Orbwalk"]["RCast" + i.ChampionName].GetValue<MenuBool>().Value).ToList();
                    if (targets.Count > 0)
                    {
                        target = TargetSelector.GetTarget(targets);
                    }
                    var selectTarget = TargetSelector.GetSelectedTarget(Q.Range + range);
                    if (selectTarget != null
                        && MainMenu["Orbwalk"]["RCast" + selectTarget.ChampionName].GetValue<MenuBool>().Value)
                    {
                        target = selectTarget;
                    }
                }
                if (RState > 0 && rShadow.IsValid())
                {
                    var targets =
                        GameObjects.EnemyHeroes.Where(i => i.IsValidTarget(Q.Range + range) && HaveRMark(i)).ToList();
                    if (targets.Count > 0)
                    {
                        var markTarget = TargetSelector.GetTarget(targets);
                        target = markTarget;
                        if (DeadByRMark(markTarget))
                        {
                            var subTarget = Q.GetTarget(
                                range,
                                false,
                                GameObjects.EnemyHeroes.Where(i => i.NetworkId == markTarget.NetworkId));
                            {
                                if (subTarget != null)
                                {
                                    target = subTarget;
                                }
                            }
                        }
                    }
                }
                return target;
            }
        }

        private static int RState
        {
            get
            {
                if (R.IsReady())
                {
                    return R.Instance.Name == "zedult" ? 0 : 1;
                }
                return R.Instance.Name == "ZedR2" ? 2 : -1;
            }
        }

        private static int WState
        {
            get
            {
                if (W.IsReady())
                {
                    return W.Instance.Name == "ZedShadowDash" && Variables.TickCount - W.LastCastAttemptT > 500 ? 0 : 1;
                }
                return -1;
            }
        }

        #endregion

        #region Methods

        private static void AutoQ()
        {
            if (!MainMenu["Hybrid"]["AutoQ"].GetValue<MenuKeyBind>().Active
                || Player.Mana < MainMenu["Hybrid"]["AutoQMpA"].GetValue<MenuSlider>().Value || !Q.IsReady())
            {
                return;
            }
            var target = Q.GetTarget();
            if (target != null)
            {
                var pred = Common.GetPrediction(Q, target, true, new[] { CollisionableObjects.YasuoWall });
                if (pred.Hitchance >= Q.MinHitChance)
                {
                    Q.Cast(pred.CastPosition);
                }
            }
        }

        private static void CastE(Obj_AI_Hero target, bool isCombo = false)
        {
            var canCombo = !MainMenu["Orbwalk"]["R"].GetValue<MenuBool>().Value
                           || !MainMenu["Orbwalk"]["RCast" + target.ChampionName].GetValue<MenuBool>().Value
                           || (Player.Distance(target) > MainMenu["Orbwalk"]["RStopRange"].GetValue<MenuSlider>().Value
                               && RState == 0) || HaveRMark(target) || rShadow.IsValid() || RState == -1;
            if (!E.IsReady() || (isCombo && !canCombo))
            {
                return;
            }
            if (E.IsInRange(target) && E.Cast())
            {
                return;
            }
            if (wShadow.IsValid() && wShadow.Distance(target) < E.Range && E.Cast())
            {
                return;
            }
            if (rShadow.IsValid() && rShadow.Distance(target) < E.Range)
            {
                E.Cast();
            }
        }

        private static void CastQ(Obj_AI_Hero target, bool isCombo = false)
        {
            var canCombo = !MainMenu["Orbwalk"]["R"].GetValue<MenuBool>().Value
                           || !MainMenu["Orbwalk"]["RCast" + target.ChampionName].GetValue<MenuBool>().Value
                           || (Player.Distance(target) > MainMenu["Orbwalk"]["RStopRange"].GetValue<MenuSlider>().Value
                               && RState == 0) || HaveRMark(target) || rShadow.IsValid() || RState == -1;
            if (!Q.IsReady() || (isCombo && !canCombo))
            {
                return;
            }
            var pred = Common.GetPrediction(Q, target, true, new[] { CollisionableObjects.YasuoWall });
            if (pred.Hitchance >= Q.MinHitChance)
            {
                Q.Cast(pred.CastPosition);
            }
            else if (wShadow.IsValid() || rShadow.IsValid())
            {
                if (wShadow.IsValid())
                {
                    Q2.UpdateSourcePosition(wShadow.Position, wShadow.Position);
                    var qPred = Common.GetPrediction(Q2, target, true, new[] { CollisionableObjects.YasuoWall });
                    if (qPred.Hitchance >= Q2.MinHitChance && Q.Cast(qPred.CastPosition))
                    {
                        return;
                    }
                }
                if (rShadow.IsValid())
                {
                    Q2.UpdateSourcePosition(rShadow.Position, rShadow.Position);
                    var qPred = Common.GetPrediction(Q2, target, true, new[] { CollisionableObjects.YasuoWall });
                    if (qPred.Hitchance >= Q2.MinHitChance)
                    {
                        Q.Cast(qPred.CastPosition);
                    }
                }
            }
        }

        private static void CastW(Obj_AI_Hero target, bool isCombo = false)
        {
            if (WState != 0 || Variables.TickCount - W.LastCastAttemptT <= 500
                || Player.Distance(target) >= W.Range + Q.Range)
            {
                return;
            }
            var castPos = default(Vector3);
            if (isCombo)
            {
                switch (MainMenu["Orbwalk"]["WAdv"].GetValue<MenuList>().Index)
                {
                    case 1:
                        castPos = target.ServerPosition.Extend(Player.ServerPosition, -W.Range);
                        break;
                    case 2:
                        var subPos1 = Player.ServerPosition
                                      + (target.ServerPosition - rShadow.Position).Normalized().Perpendicular()
                                      * W.Range;
                        var subPos2 = Player.ServerPosition
                                      + (rShadow.Position - target.ServerPosition).Normalized().Perpendicular()
                                      * W.Range;
                        if (subPos1.IsWall() && !subPos2.IsWall() && target.Distance(subPos2) < target.Distance(subPos1))
                        {
                            castPos = subPos2;
                        }
                        if (!subPos1.IsWall() && subPos2.IsWall() && target.Distance(subPos1) < target.Distance(subPos2))
                        {
                            castPos = subPos1;
                        }
                        if ((!subPos1.IsWall() && !subPos2.IsWall()) || (subPos1.IsWall() && subPos2.IsWall()))
                        {
                            castPos = target.Distance(subPos1) < target.Distance(subPos2) ? subPos1 : subPos2;
                        }
                        if (!castPos.IsValid())
                        {
                            castPos = subPos1;
                        }
                        break;
                    case 3:
                        castPos = Game.CursorPos;
                        break;
                }
            }
            else
            {
                castPos = target.ServerPosition.Extend(Player.ServerPosition, -E.Range / 2);
            }
            if (!castPos.IsValid())
            {
                return;
            }
            if (!castPos.IsWall())
            {
                if (W.Cast(castPos))
                {
                    W.LastCastAttemptT = Variables.TickCount;
                    return;
                }
            }
            for (var i = W.Range; i >= 400; i -= 25)
            {
                var castPosSub = Player.ServerPosition.Extend(castPos, i);
                if (castPosSub.IsWall() && !i.Equals(400))
                {
                    continue;
                }
                if (W.Cast(castPosSub))
                {
                    W.LastCastAttemptT = Variables.TickCount;
                    break;
                }
            }
        }

        private static bool DeadByRMark(Obj_AI_Hero target)
        {
            return deathMark != null && target.Distance(deathMark) < target.BoundingRadius;
        }

        private static List<double> GetComboDmg(Obj_AI_Hero target, bool useQ, bool useW, bool useE, bool useR)
        {
            var dmgTotal = 0d;
            var manaTotal = 0f;
            if (useQ)
            {
                dmgTotal += GetQDmg(target);
                manaTotal += Q.Instance.ManaCost;
            }
            if (useW)
            {
                if (useQ)
                {
                    dmgTotal += GetQDmg(target) / 2;
                }
                if (WState == 0)
                {
                    manaTotal += W.Instance.ManaCost;
                }
            }
            if (useE)
            {
                dmgTotal += GetEDmg(target);
                manaTotal += E.Instance.ManaCost;
            }
            dmgTotal += Player.GetAutoAttackDamage(target, true);
            if (target.HealthPercent <= 50 && !target.HasBuff("ZedPassiveCD"))
            {
                dmgTotal += Player.CalculateDamage(
                    target,
                    DamageType.Magical,
                    target.MaxHealth * (Player.Level > 16 ? 0.1 : (Player.Level > 6 ? 0.08 : 0.06)));
            }
            if (useR || HaveRMark(target))
            {
                dmgTotal += new[] { 0.2, 0.35, 0.5 }[R.Level - 1] * dmgTotal + Player.TotalAttackDamage;
            }
            return new List<double> { dmgTotal * OverkillValue, manaTotal };
        }

        private static double GetEDmg(Obj_AI_Base target)
        {
            return Player.CalculateDamage(
                target,
                DamageType.Physical,
                new[] { 60, 90, 120, 150, 180 }[E.Level - 1] + 0.8f * Player.FlatPhysicalDamageMod);
        }

        private static double GetQDmg(Obj_AI_Base target)
        {
            return Player.CalculateDamage(
                target,
                DamageType.Physical,
                new[] { 75, 115, 155, 195, 235 }[Q.Level - 1] + Player.FlatPhysicalDamageMod);
        }

        private static bool HaveRMark(Obj_AI_Hero target)
        {
            return target.HasBuff("zedulttargetmark");
        }

        private static void Hybrid()
        {
            var target = GetTarget;
            if (target == null)
            {
                return;
            }
            if (MainMenu["Hybrid"]["Mode"].GetValue<MenuList>().Index == 0
                && (!MainMenu["Hybrid"]["QOverW"].GetValue<MenuBool>().Value
                    || (!Q.IsReady() && Player.GetLastCastedSpell().SpellData.Name != "ZedShuriken"))
                && ((Q.IsReady() && Player.Mana >= Q.Instance.ManaCost + W.Instance.ManaCost)
                    || (E.IsReady() && Player.Mana >= E.Instance.ManaCost + W.Instance.ManaCost)))
            {
                CastW(target);
            }
            if (MainMenu["Hybrid"]["Mode"].GetValue<MenuList>().Index < 2)
            {
                CastE(target);
            }
            if (MainMenu["Hybrid"]["Mode"].GetValue<MenuList>().Index != 0
                || MainMenu["Hybrid"]["QOverW"].GetValue<MenuBool>().Value || wShadow.IsValid() || WState == -1
                || Player.Mana < Q.Instance.ManaCost + W.Instance.ManaCost)
            {
                CastQ(target);
            }
        }

        private static void KillSteal()
        {
            if (MainMenu["KillSteal"]["Q"].GetValue<MenuBool>().Value && Q.IsReady())
            {
                foreach (var hero in
                    GameObjects.EnemyHeroes.Where(i => i.IsValidTarget() && i.Health <= GetQDmg(i)))
                {
                    var pred = Common.GetPrediction(Q, hero, true, new[] { CollisionableObjects.YasuoWall });
                    if (pred.Hitchance >= Q.MinHitChance)
                    {
                        if (Q.Cast(pred.CastPosition))
                        {
                            break;
                        }
                    }
                    else if (wShadow.IsValid() || rShadow.IsValid())
                    {
                        if (wShadow.IsValid())
                        {
                            Q2.UpdateSourcePosition(wShadow.Position, wShadow.Position);
                            var qPred = Common.GetPrediction(Q2, hero, true, new[] { CollisionableObjects.YasuoWall });
                            if (qPred.Hitchance >= Q2.MinHitChance && Q.Cast(qPred.CastPosition))
                            {
                                break;
                            }
                        }
                        if (rShadow.IsValid())
                        {
                            Q2.UpdateSourcePosition(rShadow.Position, rShadow.Position);
                            var qPred = Common.GetPrediction(Q2, hero, true, new[] { CollisionableObjects.YasuoWall });
                            if (qPred.Hitchance >= Q2.MinHitChance)
                            {
                                Q.Cast(qPred.CastPosition);
                            }
                        }
                    }
                }
            }
            if (MainMenu["KillSteal"]["E"].GetValue<MenuBool>().Value && E.IsReady()
                && GameObjects.EnemyHeroes.Where(i => i.IsValidTarget() && i.Health <= GetEDmg(i))
                       .Any(
                           i =>
                           E.IsInRange(i) || (wShadow.IsValid() && i.Distance(wShadow) < E.Range)
                           || (rShadow.IsValid() && i.Distance(rShadow) < E.Range)))
            {
                E.Cast();
            }
        }

        private static void LastHit()
        {
            if (MainMenu["LastHit"]["Q"].GetValue<MenuBool>().Value && Q.IsReady())
            {
                foreach (var minion in
                    GameObjects.EnemyMinions.Where(i => i.IsValidTarget(Q.Range) && Q.GetHealthPrediction(i) > 0)
                        .OrderByDescending(i => i.MaxHealth))
                {
                    var pred = Common.GetPrediction(
                        Q,
                        minion,
                        true,
                        new[]
                            {
                                CollisionableObjects.Heroes, CollisionableObjects.Minions, CollisionableObjects.YasuoWall
                            });
                    if (pred.CollisionObjects.Count > 0 && pred.CollisionObjects.Any(i => i.IsMe))
                    {
                        break;
                    }
                    var qDmg = GetQDmg(minion) * (pred.CollisionObjects.Count == 0 ? 1 : 0.6);
                    if (Q.GetHealthPrediction(minion) <= qDmg)
                    {
                        Q.Cast(pred.CastPosition);
                    }
                }
            }
            if (MainMenu["LastHit"]["E"].GetValue<MenuBool>().Value && E.IsReady()
                && GameObjects.EnemyMinions.Any(
                    i =>
                    i.IsValidTarget(E.Range) && E.GetHealthPrediction(i) > 0 && E.GetHealthPrediction(i) <= GetEDmg(i)))
            {
                E.Cast();
            }
        }

        private static void OnCreate(GameObject sender, EventArgs args)
        {
            var mark = sender as Obj_GeneralParticleEmitter;
            if (mark != null && mark.IsValid && mark.Name == "Zed_Base_R_buf_tell.troy" && rShadow.IsValid()
                && deathMark == null)
            {
                deathMark = mark;
            }
            var shadow = sender as Obj_AI_Minion;
            if (shadow == null || !shadow.IsValid() || shadow.Name != "Shadow" || shadow.IsEnemy)
            {
                return;
            }
            if (wCasted)
            {
                wShadow = shadow;
                wCasted = false;
                rCasted = false;
                DelayAction.Add(4500, () => wShadow = null);
            }
            else if (rCasted)
            {
                rShadow = shadow;
                rCasted = false;
                wCasted = false;
                DelayAction.Add(7500, () => rShadow = null);
            }
        }

        private static void OnDelete(GameObject sender, EventArgs args)
        {
            var mark = sender as Obj_GeneralParticleEmitter;
            if (mark != null && mark.IsValid && mark.Name == "Zed_Base_R_buf_tell.troy" && deathMark != null)
            {
                deathMark = null;
            }
        }

        private static void OnDraw(EventArgs args)
        {
            if (Player.IsDead)
            {
                return;
            }
            if (MainMenu["Draw"]["Q"].GetValue<MenuBool>().Value && Q.Level > 0)
            {
                Drawing.DrawCircle(Player.Position, Q.Range, Q.IsReady() ? Color.LimeGreen : Color.IndianRed);
            }
            if (MainMenu["Draw"]["W"].GetValue<MenuBool>().Value && W.Level > 0)
            {
                Drawing.DrawCircle(Player.Position, W.Range, W.IsReady() ? Color.LimeGreen : Color.IndianRed);
            }
            if (MainMenu["Draw"]["E"].GetValue<MenuBool>().Value && E.Level > 0)
            {
                Drawing.DrawCircle(Player.Position, E.Range, E.IsReady() ? Color.LimeGreen : Color.IndianRed);
            }
            if (MainMenu["Draw"]["R"].GetValue<MenuBool>().Value && R.Level > 0)
            {
                Drawing.DrawCircle(Player.Position, R.Range, R.IsReady() ? Color.LimeGreen : Color.IndianRed);
            }
            if (MainMenu["Draw"]["Target"].GetValue<MenuBool>().Value)
            {
                var target = GetTarget;
                if (target != null)
                {
                    Drawing.DrawCircle(target.Position, target.BoundingRadius * 1.5f, Color.Aqua);
                }
            }
            if (MainMenu["Draw"]["WPos"].GetValue<MenuBool>().Value && wShadow.IsValid())
            {
                Drawing.DrawCircle(wShadow.Position, wShadow.BoundingRadius * 1.5f, Color.White);
                var pos = Drawing.WorldToScreen(wShadow.Position);
                Drawing.DrawText(pos.X, pos.Y, Color.Red, "W");
            }
            if (MainMenu["Draw"]["RPos"].GetValue<MenuBool>().Value && rShadow.IsValid())
            {
                Drawing.DrawCircle(rShadow.Position, rShadow.BoundingRadius * 1.5f, Color.White);
                var pos = Drawing.WorldToScreen(rShadow.Position);
                Drawing.DrawText(pos.X, pos.Y, Color.Red, "R");
            }
        }

        private static void OnProcessSpellCast(Obj_AI_Base sender, GameObjectProcessSpellCastEventArgs args)
        {
            if (!sender.IsMe)
            {
                return;
            }
            if (args.SData.Name == "ZedShuriken" && Orbwalker.ActiveMode == OrbwalkerMode.Hybrid
                && MainMenu["Hybrid"]["Mode"].GetValue<MenuList>().Index == 0
                && MainMenu["Hybrid"]["QOverW"].GetValue<MenuBool>().Value && WState == 0 && GetTarget != null)
            {
                DelayAction.Add(
                    Q.Delay * 1000,
                    () =>
                        {
                            if (W.Cast(args.End))
                            {
                                W.LastCastAttemptT = Variables.TickCount;
                            }
                        });
            }
            if (args.SData.Name == "ZedPBAOEDummy" && eForced)
            {
                eForced = false;
            }
            if (args.SData.Name == "ZedShadowDash")
            {
                rCasted = false;
                wCasted = true;
            }
            if (args.SData.Name == "zedult")
            {
                wCasted = false;
                rCasted = true;
                eForced = true;
                DelayAction.Add(1000, () => eForced = false);
            }
        }

        private static void OnUpdate(EventArgs args)
        {
            if (Player.IsDead || MenuGUI.IsChatOpen || Player.IsRecalling())
            {
                return;
            }
            KillSteal();
            switch (Orbwalker.ActiveMode)
            {
                case OrbwalkerMode.Orbwalk:
                    Orbwalk();
                    break;
                case OrbwalkerMode.Hybrid:
                    Hybrid();
                    break;
                case OrbwalkerMode.LastHit:
                    LastHit();
                    break;
            }
            if (Orbwalker.ActiveMode != OrbwalkerMode.Orbwalk && Orbwalker.ActiveMode != OrbwalkerMode.Hybrid)
            {
                AutoQ();
            }
            if (MainMenu["FleeW"].GetValue<MenuKeyBind>().Active)
            {
                Player.IssueOrder(GameObjectOrder.MoveTo, Game.CursorPos);
                if (W.IsReady())
                {
                    W.Cast(Game.CursorPos);
                }
            }
        }

        private static void Orbwalk()
        {
            if (RState == 1 && rShadow.IsValid()
                && Player.HealthPercent < MainMenu["Orbwalk"]["RSwapIfHpU"].GetValue<MenuSlider>().Value
                && Common.CountEnemy(Q.Range) > Common.CountEnemy(W.Range, rShadow.Position) && R.Cast())
            {
                return;
            }
            if (eForced && E.IsReady() && Common.CountEnemy(E.Range) > 0 && E.Cast())
            {
                return;
            }
            var target = GetTarget;
            if (target != null)
            {
                if (MainMenu["Orbwalk"]["WSwapGap"].GetValue<MenuList>().Index > 0 && WState == 1 && wShadow.IsValid()
                    && Player.Distance(target) > wShadow.Distance(target) && !E.IsInRange(target)
                    && !DeadByRMark(target))
                {
                    if (MainMenu["Orbwalk"]["WSwapGap"].GetValue<MenuList>().Index == 1)
                    {
                        var calcCombo = GetComboDmg(
                            target,
                            Q.IsReady(),
                            wShadow.Distance(target) < Q.Range,
                            E.IsReady(),
                            MainMenu["Orbwalk"]["R"].GetValue<MenuBool>().Value
                            && MainMenu["Orbwalk"]["RCast" + target.ChampionName].GetValue<MenuBool>().Value
                            && RState == 0);
                        if (((target.Health < calcCombo[0]
                              && (Player.Mana >= calcCombo[1] || Player.Mana * OverkillValue >= calcCombo[1]))
                             || (MainMenu["Orbwalk"]["R"].GetValue<MenuBool>().Value
                                 && MainMenu["Orbwalk"]["RCast" + target.ChampionName].GetValue<MenuBool>().Value
                                 && RState == 0 && !R.IsInRange(target) && wShadow.Distance(target) < R.Range))
                            && W.Cast())
                        {
                            return;
                        }
                    }
                    else if (!Q.IsReady() && W.Cast())
                    {
                        return;
                    }
                }
                if (MainMenu["Orbwalk"]["RSwapGap"].GetValue<MenuList>().Index > 0 && RState == 1 && rShadow.IsValid()
                    && Player.Distance(target) > rShadow.Distance(target) && !E.IsInRange(target)
                    && !DeadByRMark(target))
                {
                    if (MainMenu["Orbwalk"]["RSwapGap"].GetValue<MenuList>().Index == 1)
                    {
                        var calcCombo = GetComboDmg(target, Q.IsReady(), wShadow.IsValid(), E.IsReady(), false);
                        if (((target.Health < calcCombo[0]
                              && (Player.Mana >= calcCombo[1] || Player.Mana * OverkillValue >= calcCombo[1]))
                             || (MainMenu["Orbwalk"]["WAdv"].GetValue<MenuList>().Index > 0 && WState == 0
                                 && !W.IsInRange(target) && rShadow.Distance(target) < W.Range
                                 && ((Q.IsReady() && Player.Mana >= Q.Instance.ManaCost + W.Instance.ManaCost)
                                     || (E.IsReady() && Player.Mana >= E.Instance.ManaCost + W.Instance.ManaCost))))
                            && R.Cast())
                        {
                            return;
                        }
                    }
                    else if (!Q.IsReady() && R.Cast())
                    {
                        return;
                    }
                }
                if (RState == 1 && rShadow.IsValid() && MainMenu["Orbwalk"]["RSwapIfKill"].GetValue<MenuBool>().Value
                    && DeadByRMark(target) && Common.CountEnemy(Q.Range) > Common.CountEnemy(W.Range, rShadow.Position)
                    && R.Cast())
                {
                    return;
                }
                if (RState == 0 && MainMenu["Orbwalk"]["R"].GetValue<MenuBool>().Value
                    && MainMenu["Orbwalk"]["RCast" + target.ChampionName].GetValue<MenuBool>().Value
                    && R.IsInRange(target) && R.CastOnUnit(target))
                {
                    return;
                }
                if (Ignite.IsReady() && ((HaveRMark(target) && rShadow.IsValid()) || target.HealthPercent < 30)
                    && Player.Distance(target) <= 600)
                {
                    Player.Spellbook.CastSpell(Ignite, target);
                }
                if ((Q.IsReady() && Player.Mana >= Q.Instance.ManaCost + W.Instance.ManaCost)
                    || (E.IsReady() && Player.Mana >= E.Instance.ManaCost + W.Instance.ManaCost))
                {
                    if (MainMenu["Orbwalk"]["WNormal"].GetValue<MenuBool>().Value)
                    {
                        if (RState < 1
                            && (!MainMenu["Orbwalk"]["R"].GetValue<MenuBool>().Value
                                || !MainMenu["Orbwalk"]["RCast" + target.ChampionName].GetValue<MenuBool>().Value
                                || RState == -1))
                        {
                            CastW(target);
                        }
                        if (RState > 0 && MainMenu["Orbwalk"]["R"].GetValue<MenuBool>().Value
                            && MainMenu["Orbwalk"]["RCast" + target.ChampionName].GetValue<MenuBool>().Value
                            && !HaveRMark(target) && rShadow.IsValid())
                        {
                            CastW(target);
                        }
                    }
                    if (MainMenu["Orbwalk"]["WAdv"].GetValue<MenuList>().Index > 0 && RState > 0
                        && MainMenu["Orbwalk"]["R"].GetValue<MenuBool>().Value
                        && MainMenu["Orbwalk"]["RCast" + target.ChampionName].GetValue<MenuBool>().Value
                        && HaveRMark(target) && rShadow.IsValid())
                    {
                        CastW(target, true);
                    }
                }
                CastQ(target, true);
                CastE(target, true);
            }
            UseItem(target);
        }

        private static void UseItem(Obj_AI_Hero target)
        {
            if (target != null
                && ((HaveRMark(target) && rShadow.IsValid()) || target.HealthPercent < 40 || Player.HealthPercent < 50))
            {
                if (Bilgewater.IsReady)
                {
                    Bilgewater.Cast(target);
                }
                if (BotRuinedKing.IsReady)
                {
                    BotRuinedKing.Cast(target);
                }
            }
            if (Youmuu.IsReady && Common.CountEnemy(R.Range + 200) > 0)
            {
                Youmuu.Cast();
            }
            if (Tiamat.IsReady && Common.CountEnemy(Tiamat.Range) > 0)
            {
                Tiamat.Cast();
            }
            if (Hydra.IsReady && Common.CountEnemy(Hydra.Range) > 0)
            {
                Hydra.Cast();
            }
            if (Titanic.IsReady && Common.CountEnemy(Titanic.Range) > 0)
            {
                Titanic.Cast();
            }
        }

        #endregion
    }
}