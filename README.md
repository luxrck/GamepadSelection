## GamepadTweaks

[![Build](https://img.shields.io/github/workflow/status/luxrck/GamepadTweaks/Build?style=for-the-badge)](https://github.com/luxrck/GamepadTweaks/blob/master/.github/workflows/build.yml)

❗: This plugin is under heavy development now and may contains many potential bugs, so use at your own risk.

##### Gamepad Selection
Using `y/b/a/x` and Dpad `up/right/down/left` buttons to directly select party member no.1 ~ no.8 when casting monitored **single-target** actions.

**Step 1**: Trigger an monitored action, like `Haima`, entering gamepad selection mode (instead of execute it directly).

**Step 2**: Press a button to select a party member and execute that action to the target immediately.

❗: Those button states are only captured **when CrossHotBar is activated (press LT/RT)**

##### Gtoff
Support and extend `<gtoff>` like casting type using gamepad. If we already target a GameObject (enemy, party member, etc), then it will use the position of the target (`/ac xx <t>`). Otherwise it acts just like `<gtoff>` macros (`/ac xx <gtoff>`).

vs. `/ac xx <t>`: We put GtAction into queue and execute it when ready instead of must wait a certain time to manually trigger that action. If `actionAutoDelay == true`, these two are same.

##### Targeting
Auto targeting the nearest enemy (if not) when casting actions. Damage actions will ignore the **SoftTarget** now, only Buffs could take use of it.

##### Combo
Combo support like [XivComboPlugin](https://github.com/attickdoor/XIVComboPlugin.git), but could take use of our own tweaks.

##### Macro
Auto delay `/ac` commands to the right time.

### CLI

```
/gt → Open setting panel.
/gt on/off → Enable/Disable this plugin.
/gt list → List actions and corresponding selection order.
/gt add <action> [<selectOrder>] → Add specific <action> in monitor.
/gt remove <action> → Remove specific monitored <action>.
/gt reset [<action>] → Reset combo index for given group (reset all if <action> not given).
/gt id <action> → Show Action ID.

<action>        Action name (in string).
<selectOrder>   The order for party member selection (only accepted Dpad and y/b/a/x buttons (Xbox)).
```

### Config
```yaml
# Always treating yourself as a member in a party list (even not exists).
alwaysInParty: true

# Auto targeting the nearest enemy when casting actions.
autoTargeting: true
alwaysTargetingNearestEnemy: false

# Serial execute /ac commands in macro.
actionAutoDelay: false

# Default select order for gs actions.
# y → left <=> party member no.1 → no.8
priority: y b a x up right down left

# Emulate <gtoff>
# Casting direcly instead of entering ground targeting mode.
gtoff:
  - 地星
  - 野战治疗阵

# Actions using gamepad selection.
gs:
  - 均衡诊断
  - 白牛清汁
  - 灵橡清汁
  - 输血

  - 吉星相位
  - 先天禀赋
  - 星位合图
  - 天星交错
  - 出卡
  - 擢升

  - 生命活性法
  - 生命回生法
  - 深谋远虑之策
  - 以太契约

# <combo type> : <combo chain> : <slot action>
# m:  Manual
# l:  Linear
# s:  Strict
# lb: LinearBlocked
# sb: StrictBlocked
# o:  Ochain
combo: |-
  o 出卡    : 抽卡! -> 出卡? : 出卡
  o 出王冠卡: 小奥秘卡! -> 出王冠卡? : 出王冠卡
  o 宝石辉  : 以太蓄能! -> 星极超流 -> 龙神迸发 ->
              绿宝石召唤! -> 宝石辉{4}? ->
              黄宝石召唤! -> 宝石辉{4}? ->
              红宝石召唤! -> 宝石辉{2}?
  o 宝石耀  : 以太蓄能! -> 星极超流 -> 龙神迸发 ->
              绿宝石召唤! -> 宝石耀{4}? ->
              黄宝石召唤! -> 宝石耀{4}? ->
              红宝石召唤! -> 宝石耀{2}?
  o 暴风斩  : 重劈#1 -> 凶残裂#1 -> 暴风斩#1 : 暴风斩
  o 暴风碎  : 重劈#1 -> 凶残裂#1 -> 暴风碎#1 : 暴风碎
  o 超压斧  : 超压斧#1 -> 秘银暴风#1 : 超压斧
  o 狂暴    : 狂暴 -> 蛮荒崩裂* : 狂暴

# Key: Action / ActionID
# Val: Select order string, will use `priority` value in config if val is "" or "default".
rules:
  # Use a different select order for specific action.
  Aspected Benefic: y a x b up down left right

  # Could add other actions which this plugin are not built-in.
  # ActionID: 16556 → "Celestial Intersection"
  "16556": default
```

### Pre-included Actions

See [assets/Actions.json](assets/Actions.json), full Actions could be seen at [Actions.csv](https://github.com/xivapi/ffxiv-datamining/csv/Action.csv) for XivGlobal or [Actions.CN.csv](https://github.com/thewakingsands/ffxiv-datamining-cn/Action.csv) for XivCN.