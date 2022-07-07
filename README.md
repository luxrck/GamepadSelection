## Gamepad Selection
Using `y/b/a/x` and Dpad `up/right/down/left` buttons to directly select party member no.1-8.

**Step 1**: Trigger an monitored action, like `Haima`, this not execute it.

**Step 2**: Press a button to select a party member and execute that action to the target immediately.

❗: Those button states are only captured **when CrossHotBar is activated (press LT/RT)**

### gtoff
Support and extend `<gtoff>` like casting type using gamepad. If we already target a GameObject (enemy, party member, etc), then it will use the position of the target (`/ac xx <t>`). Otherwise it acts just like `<gtoff>` macros (`/ac xx <gtoff>`).

### CLI

```
/gi → Open setting panel.
/gi list → List actions and corresponding selection order.
/gi add <action> [<selectOrder>] → Add specific <action> in monitor.
/gi remove <action> → Remove specific monitored <action>.

<action>        Action name (in string).
<selectOrder>   The order for party member selection (only accepted Dpad and y/b/a/x buttons).
   Xbox |   PS
    y   |   △   |   n:North
    b   |   ○   |   e:East
    a   |   x   |   s:South
    x   |   □   |   w:West
```

### Config
```jsonc
{
  // Always treating yourself as a member in a party list (even not exists).
  "alwaysInParty": false,

  // Emulate <gtoff>
  // Casting direcly instead of entering ground targeting mode.
  "gtoff": [
    "地星"
  ],

  // Actions using gamepad selection.
  "gs": [
    "均衡诊断",
    "Haima"
  ],

  // Default select order for gs actions.
  // y → left <=> party member no.1 → no.8
  "priority": "y b a x up right down left",

  // Key: Action / ActionID
  // Val: Select order string, will use `priority` value in config if val is "" or "default".
  "rules": {
    // Use a different select order for specific action.
    "Aspected Benefic": "y a x b up down left right",

    // Could add other actions which this plugin are not built-in.
    // ActionID: 16556 → "Celestial Intersection"
    "16556": "default",
  }
}
```

### Pre-included Actions
```csharp
{
  // SGE
  {"均衡诊断", 24291},
  {"白牛清汁", 24303},
  {"灵橡清汁", 24296},
  {"混合", 24317},
  {"输血", 24305},
  {"Diagnosis", 24284},
  {"Eukrasian Diagnosis", 24291},
  {"Taurochole", 24303},
  {"Druochole", 24296},
  {"Krasis", 24317},
  {"Haima", 24305},

  // WHM
  {"再生", 137},
  {"天赐祝福", 140},
  {"神祝祷", 7432},
  {"神名", 3570},
  {"水流幕", 25861},
  {"安慰之心", 16531},
  {"庇护所", 3569},
  {"礼仪之铃", 25862},
  {"Regen", 137},
  {"Benediction", 140},
  {"Divine Benison", 7432},
  {"Tetragrammaton", 3570},
  {"Aquaveil", 25861},
  {"Afflatus Solace", 16531},
  {"Asylum", 3569},
  {"Liturgy of the Bell", 25862},

  // AST
  {"先天禀赋", 3614},
  {"出卡", 17055},
  {"吉星相位", 3595},
  {"星位合图", 3612},
  {"出王冠卡", 25869},
  {"天星交错", 16556},
  {"擢升", 25873},
  {"地星", 7439},
  {"Essential Dignity", 3614},
  {"Play", 17055},
  {"Aspected Benefic", 3595},
  {"Synastry", 3612},
  {"Crown Play", 25869},
  {"Celestial Intersection", 16556},
  {"Exaltation", 25873},

  // SCH
  {"鼓舞激励之策", 185},
  {"生命活性法", 189},
  {"深谋远虑之策", 7434},
  {"以太契约", 7423},
  {"生命回生法", 25867},
  {"Adloquium", 185},
  {"Lustrate", 189},
  {"Excogitation", 7434},
  {"Aetherpact", 7423},
  {"Protraction", 25867},
}
```
