/*!
 * Cities: Skylines II UI Module
 *
 * Id: AutomaticTextureReplacer
 * Version: 0.1.0
 * Dependencies:
 */
const register = (moduleRegistry) => {
  const React = window.React;
  const api = window["cs2/api"];
  const ui = window["cs2/ui"] || {};
  if (!React || !api) {
    console.log("[AutomaticTextureReplacer] UI globals are unavailable");
    return;
  }

  const { bindValue, trigger, useValue } = api;
  const h = React.createElement;
  const GameCheckbox = moduleRegistry.get("game-ui/common/input/toggle/checkbox/checkbox.tsx", "Checkbox");
  const FocusDisabled = moduleRegistry.get("game-ui/common/focus/focus-key.ts", "FOCUS_DISABLED");
  const surfaceCatalog$ = bindValue("automatictexturereplacer", "surfaceCatalog", "[]");
  const surfacePickerOpen$ = bindValue("automatictexturereplacer", "surfacePickerOpen", false);
  const surfacePickerX$ = bindValue("automatictexturereplacer", "surfacePickerX", -1);
  const surfacePickerY$ = bindValue("automatictexturereplacer", "surfacePickerY", -1);
  const surfacePickerWidth$ = bindValue("automatictexturereplacer", "surfacePickerWidth", 920);
  const surfacePickerHeight$ = bindValue("automatictexturereplacer", "surfacePickerHeight", 680);
  const dialogFontSize$ = bindValue("automatictexturereplacer", "dialogFontSize", 100);
  const RuleRow = React.memo(({ rule, deleteRule, setRuleEnabled, scaledFontSize }) => {
    const gameButton = (label, onSelect, variant = "flat", title, className) => ui.Button
      ? h(ui.Button, { variant, focusKey: FocusDisabled, title, tooltipLabel: title, onSelect, className, style: { fontSize: scaledFontSize } }, label)
      : h("button", { title, onClick: onSelect, className, style: { fontSize: scaledFontSize } }, label);
    const checkbox = GameCheckbox
      ? h(GameCheckbox, { checked: rule.enabled, focusKey: FocusDisabled, onChange: (enabled) => setRuleEnabled(rule.id, enabled) })
      : h("span", null, rule.enabled ? "✓" : "");
    const surfaces = (title, side) => h("div", { className: "atrSurfaceGroup" },
      gameButton("Edit", () => trigger("automatictexturereplacer", "openSurfacePicker"), "flat", title, "atrEditButton"),
      h("span", { className: "atrSurfacePlaceholder", style: { fontSize: scaledFontSize } }, "…some icons…"));
    const actions = h("div", { className: "atrRuleActions" },
      gameButton("Replace", () => {}, "primary"),
      gameButton("×", () => deleteRule(rule.id), "flat", "Delete rule"));

    return h(ui.PanelSectionRow, {
      disableFocus: true,
      className: "atrRuleRow",
      left: h("div", { className: "atrSurfaceGroup" }, checkbox, surfaces("Edit source surfaces", "source")),
      center: surfaces("Edit replacement surfaces", "target"),
      right: actions,
    });
  });
  const dialogOpen$ = bindValue("automatictexturereplacer", "dialogOpen", true);
  const dialogX$ = bindValue("automatictexturereplacer", "dialogX", -1);
  const dialogY$ = bindValue("automatictexturereplacer", "dialogY", -1);
  const dialogWidth$ = bindValue("automatictexturereplacer", "dialogWidth", 440);
  const dialogHeight$ = bindValue("automatictexturereplacer", "dialogHeight", 300);
  const ToolbarButton = () => {
    const open = useValue(dialogOpen$);
    const toggle = () => trigger("automatictexturereplacer", "toggleDialog");
    const props = {
      variant: "floating",
      src: "coui://automatictexturereplacer/images/AutomaticTextureReplacer_Icon.svg",
      selected: open,
      tooltipLabel: "Automatic Texture Replacer",
      onSelect: toggle,
    };
    return ui.Button
      ? h(ui.Button, props)
      : h("button", { className: "atrToolbarFallback", onClick: toggle, title: "Automatic Texture Replacer" }, "ATR");
  };

  const SurfacePicker = () => {
    const open = useValue(surfacePickerOpen$);
    const savedX = useValue(surfacePickerX$);
    const savedY = useValue(surfacePickerY$);
    const savedWidth = useValue(surfacePickerWidth$);
    const savedHeight = useValue(surfacePickerHeight$);
    const fontSize = useValue(dialogFontSize$);
    const catalogJson = useValue(surfaceCatalog$);
    const [selectedId, setSelectedId] = React.useState(null);
    const [collapsedGroups, setCollapsedGroups] = React.useState({});
    const [dragPosition, setDragPosition] = React.useState(null);
    const [size, setSize] = React.useState(null);
    const dialogRef = React.useRef(null);
    const catalog = React.useMemo(() => {
      try {
        return JSON.parse(catalogJson || "[]");
      } catch (error) {
        console.error("[AutomaticTextureReplacer] Invalid surface catalog", error);
        return [];
      }
    }, [catalogJson]);
    const groups = React.useMemo(() => {
      const grouped = new Map();
      catalog.forEach((surface) => {
        const name = surface.group || "In-game asset";
        if (!grouped.has(name)) grouped.set(name, []);
        grouped.get(name).push(surface);
      });
      return Array.from(grouped, ([name, surfaces]) => ({ name, surfaces }))
        .sort((first, second) => {
          const priority = (name) => name === "In-game" ? 0 : name === "Special Industries" ? 1 : 2;
          const order = priority(first.name) - priority(second.name);
          return order || first.name.localeCompare(second.name, undefined, { sensitivity: "base" });
        });
    }, [catalog]);
    React.useEffect(() => {
      if (open) setSelectedId(null);
    }, [open]);

    if (!open) return null;

    const initialX = savedX < 0 ? Math.max(0, Math.round((window.innerWidth - savedWidth) / 2)) : savedX;
    const initialY = savedY < 0 ? Math.max(0, Math.round((window.innerHeight - savedHeight) / 2)) : savedY;
    const position = dragPosition || { x: initialX, y: initialY };
    const dimensions = size || { width: savedWidth, height: savedHeight };

    const beginDrag = (event) => {
      if (event.button !== 0 || event.target.closest("button")) return;
      const rect = dialogRef.current.getBoundingClientRect();
      const startX = event.clientX;
      const startY = event.clientY;
      const move = (e) => setDragPosition({ x: rect.left + e.clientX - startX, y: rect.top + e.clientY - startY });
      const end = (e) => {
        window.removeEventListener("mousemove", move);
        window.removeEventListener("mouseup", end);
        const x = Math.max(0, Math.round(rect.left + e.clientX - startX));
        const y = Math.max(0, Math.round(rect.top + e.clientY - startY));
        setDragPosition({ x, y });
        trigger("automatictexturereplacer", "setSurfacePickerPosition", x + "," + y);
      };
      window.addEventListener("mousemove", move);
      window.addEventListener("mouseup", end);
    };

    const beginResize = (event, fromLeft = false) => {
      event.preventDefault();
      event.stopPropagation();
      const startX = event.clientX;
      const startY = event.clientY;
      const startWidth = dimensions.width;
      const startHeight = dimensions.height;
      const startLeft = position.x;
      const horizontalDelta = (e) => (e.clientX - startX) * (fromLeft ? -1 : 1);
      const move = (e) => {
        const width = Math.max(520, startWidth + horizontalDelta(e));
        setSize({ width, height: Math.max(360, startHeight + e.clientY - startY) });
        if (fromLeft) setDragPosition((current) => ({ ...(current || position), x: startLeft + startWidth - width }));
      };
      const end = (e) => {
        window.removeEventListener("mousemove", move);
        window.removeEventListener("mouseup", end);
        const width = Math.max(520, Math.min(1800, Math.round(startWidth + horizontalDelta(e))));
        const height = Math.max(360, Math.min(1300, Math.round(startHeight + e.clientY - startY)));
        setSize({ width, height });
        if (fromLeft) {
          const x = Math.max(0, Math.round(startLeft + startWidth - width));
          setDragPosition((current) => ({ ...(current || position), x }));
          trigger("automatictexturereplacer", "setSurfacePickerPosition", x + "," + position.y);
        }
        trigger("automatictexturereplacer", "setSurfacePickerSize", width + "," + height);
      };
      window.addEventListener("mousemove", move);
      window.addEventListener("mouseup", end);
    };

    const scaledFontSize = Math.round(13 * fontSize) / 100 + "rem";
    const button = (label, onSelect, variant) => ui.Button
      ? h(ui.Button, { variant, focusKey: FocusDisabled, onSelect, style: { fontSize: scaledFontSize } }, label)
      : h("button", { onClick: onSelect, style: { fontSize: scaledFontSize } }, label);
    const closePicker = () => trigger("automatictexturereplacer", "closeSurfacePicker");
    const renderTile = (surface) => {
      const tooltip = h("div", { className: "atrSurfaceTooltip", style: { fontSize: scaledFontSize } },
        h("strong", null, surface.name),
        h("span", null, surface.menuPath || "In-game asset"),
        h("img", { className: "atrSurfaceTooltipImage", src: surface.tooltipThumbnail || surface.thumbnail, alt: surface.name }));
      const tileContents = h(React.Fragment, null,
        h("img", { src: surface.thumbnail, alt: surface.name }),
        surface.isMod === "True" ? h("img", { className: "atrParadoxModsBadge", src: "coui://game/UI/Media/Glyphs/ParadoxMods.svg", alt: "Paradox Mods" }) : null);
      const tile = ui.Button
        ? h(ui.Button, {
          variant: selectedId === surface.id ? "primary" : "flat",
          className: "atrSurfaceTile",
          focusKey: FocusDisabled,
          onSelect: () => setSelectedId(surface.id),
          "aria-label": surface.name,
        }, tileContents)
        : h("button", { className: "atrSurfaceTile", onClick: () => setSelectedId(surface.id), title: surface.name },
          tileContents);
      return ui.Tooltip
        ? h(ui.Tooltip, { key: surface.id, tooltip, delayTime: 0, hideOnInteraction: false }, tile)
        : h("div", { key: surface.id, title: surface.name }, tile);
    };
    const sections = groups.map((group) => {
      const collapsed = !!collapsedGroups[group.name];
      const toggle = () => setCollapsedGroups((current) => ({ ...current, [group.name]: !current[group.name] }));
      const header = ui.Button
        ? h(ui.Button, {
          variant: "flat",
          className: "atrPickerGroupHeader",
          focusKey: FocusDisabled,
          onSelect: toggle,
          "aria-expanded": !collapsed,
        },
        h("span", { className: "atrPickerGroupArrow", "aria-hidden": true }, collapsed ? "▲" : "▼"),
        h("strong", null, group.name),
        h("span", { className: "atrPickerGroupCount" }, String(group.surfaces.length)))
        : h("button", { className: "atrPickerGroupHeader", onClick: toggle, "aria-expanded": !collapsed },
          h("span", { className: "atrPickerGroupArrow", "aria-hidden": true }, collapsed ? "▲" : "▼"),
          h("strong", null, group.name),
          h("span", { className: "atrPickerGroupCount" }, String(group.surfaces.length)));
      return h("section", { key: group.name, className: "atrPickerGroupSection" },
        header,
        collapsed ? null : h("div", { className: "atrSurfaceGrid" }, group.surfaces.map(renderTile)));
    });

    const style = {
      left: Math.max(0, Math.min(position.x, window.innerWidth - 100)) + "px",
      top: Math.max(0, Math.min(position.y, window.innerHeight - 80)) + "px",
      width: dimensions.width + "px",
      height: dimensions.height + "px",
    };
    const body = h("div", { className: "atrPickerBody", style: { fontSize: scaledFontSize } },
      catalog.length === 0
        ? h("div", { className: "atrPickerEmpty" }, "No surfaces found.")
        : h(ui.Scrollable || "div", { className: "atrPickerScroll", vertical: true, useNewStyle: true },
          h("div", { className: "atrPickerGroups" }, sections)));
    const footer = h("div", { className: "atrPickerFooter" },
      button("Apply", closePicker, "primary"),
      button("Cancel", closePicker, "flat"));
    const resizeCorners = h(React.Fragment, null,
      h("div", { className: "atrPickerResizeCorner atrResizeLeft", onMouseDown: (event) => beginResize(event, true), title: "Drag this corner to resize" }),
      h("div", { className: "atrPickerResizeCorner atrResizeRight", onMouseDown: (event) => beginResize(event), title: "Drag this corner to resize" }));

    return h("div", { className: "atrDialogPositioner atrPickerPositioner", style, ref: dialogRef },
      h(ui.Panel, {
        className: "atrPickerPanel",
        contentClassName: "atrPickerPanelContent",
      header: h("div", { className: "atrDialogHeader atrPickerHeader", onMouseDownCapture: beginDrag }, "Select a surface"),
        onClose: closePicker,
        footer,
      }, body), resizeCorners);
  };

  const Dialog = () => {
    const open = useValue(dialogOpen$);
    const savedX = useValue(dialogX$);
    const savedY = useValue(dialogY$);
    const savedWidth = useValue(dialogWidth$);
    const savedHeight = useValue(dialogHeight$);
    const fontSize = useValue(dialogFontSize$);
    const dialogRef = React.useRef(null);
    const rulesScrollRef = React.useRef(null);
    const nextRuleId = React.useRef(1);
    const [dragPosition, setDragPosition] = React.useState(null);
    const [size, setSize] = React.useState(null);
    const [rules, setRules] = React.useState([]);
    const deleteRule = React.useCallback((id) => {
      setRules((current) => current.filter((item) => item.id !== id));
    }, []);
    const setRuleEnabled = React.useCallback((id, enabled) => {
      setRules((current) => current.map((item) => item.id === id ? { ...item, enabled } : item));
    }, []);

    if (!open) return null;

    const initialX = savedX < 0 ? Math.max(0, Math.round((window.innerWidth - savedWidth) / 2)) : savedX;
    const initialY = savedY < 0 ? Math.max(0, Math.round((window.innerHeight - savedHeight) / 2)) : savedY;
    const position = dragPosition || { x: initialX, y: initialY };
    const dimensions = size || { width: savedWidth, height: savedHeight };

    const beginDrag = (event) => {
      if (event.button !== 0 || event.target.closest("button")) return;
      const rect = dialogRef.current.getBoundingClientRect();
      const startX = event.clientX;
      const startY = event.clientY;
      const move = (e) => setDragPosition({ x: rect.left + e.clientX - startX, y: rect.top + e.clientY - startY });
      const end = (e) => {
        window.removeEventListener("mousemove", move);
        window.removeEventListener("mouseup", end);
        const x = Math.max(0, Math.round(rect.left + e.clientX - startX));
        const y = Math.max(0, Math.round(rect.top + e.clientY - startY));
        setDragPosition({ x, y });
        trigger("automatictexturereplacer", "setDialogPosition", x + "," + y);
      };
      window.addEventListener("mousemove", move);
      window.addEventListener("mouseup", end);
    };

    const beginResize = (event, fromLeft = false) => {
      event.preventDefault();
      event.stopPropagation();
      const startX = event.clientX;
      const startY = event.clientY;
      const startWidth = dimensions.width;
      const startHeight = dimensions.height;
      const startLeft = position.x;
      const horizontalDelta = (e) => (e.clientX - startX) * (fromLeft ? -1 : 1);
      const move = (e) => {
        const width = Math.max(320, startWidth + horizontalDelta(e));
        setSize({ width, height: Math.max(200, startHeight + e.clientY - startY) });
        if (fromLeft) setDragPosition((current) => ({ ...(current || position), x: startLeft + startWidth - width }));
      };
      const end = (e) => {
        window.removeEventListener("mousemove", move);
        window.removeEventListener("mouseup", end);
        const width = Math.max(320, Math.min(1600, Math.round(startWidth + horizontalDelta(e))));
        const height = Math.max(200, Math.min(1200, Math.round(startHeight + e.clientY - startY)));
        setSize({ width, height });
        if (fromLeft) {
          const x = Math.max(0, Math.round(startLeft + startWidth - width));
          setDragPosition((current) => ({ ...(current || position), x }));
          trigger("automatictexturereplacer", "setDialogPosition", x + "," + position.y);
        }
        trigger("automatictexturereplacer", "setDialogSize", width + "," + height);
      };
      window.addEventListener("mousemove", move);
      window.addEventListener("mouseup", end);
    };

    const addRule = () => {
      const newRule = { id: nextRuleId.current++, enabled: true };
      setRules((current) => [...current, newRule]);
      window.requestAnimationFrame(() => {
        const scrollArea = rulesScrollRef.current;
        if (scrollArea) scrollArea.scrollTop = scrollArea.scrollHeight;
      });
    };

    const rows = rules.map((rule) => h(RuleRow, {
      key: rule.id,
      rule,
      deleteRule,
      setRuleEnabled,
      scaledFontSize: Math.round(13 * fontSize) / 100 + "rem",
    }));

    const style = {
      left: Math.max(0, Math.min(position.x, window.innerWidth - 80)) + "px",
      top: Math.max(0, Math.min(position.y, window.innerHeight - 50)) + "px",
      width: dimensions.width + "px",
      height: dimensions.height + "px",
    };

    const scaledFontSize = Math.round(13 * fontSize) / 100 + "rem";
    const gameButton = (label, onSelect, variant = "flat") => ui.Button
      ? h(ui.Button, { variant, focusKey: FocusDisabled, onSelect, style: { fontSize: scaledFontSize } }, label)
      : h("button", { onClick: onSelect, style: { fontSize: scaledFontSize } }, label);
    const body = h("div", { className: "atrDialogBody", style: { fontSize: scaledFontSize } },
      h("div", { className: "atrActiveArea" },
      h("div", { className: "atrRulesToolbar" }, gameButton("Add", addRule, "primary")),
      h(ui.PanelSectionRow, {
        disableFocus: true,
        className: "atrRulesHeader",
        left: h("div", { className: "atrHeaderSource" },
          h("span", { className: "atrCheckboxSpacer" }),
          h("strong", { style: { fontSize: scaledFontSize } }, "Surfaces to replace")),
        center: h("strong", { style: { fontSize: scaledFontSize } }, "Replacement surfaces"),
        right: null,
      }),
      h(ui.Scrollable || "div", {
        className: "atrRulesScroll",
        ref: rulesScrollRef,
        vertical: true,
        horizontal: true,
        useNewStyle: true,
      }, rows)));
    const resizeCorners = h(React.Fragment, null,
      h("div", { className: "atrResizeCorner atrResizeLeft", onMouseDown: (event) => beginResize(event, true), title: "Drag this corner to resize" }),
      h("div", { className: "atrResizeCorner atrResizeRight", onMouseDown: (event) => beginResize(event), title: "Drag this corner to resize" }));

    if (ui.Panel) {
      return h("div", { className: "atrDialogPositioner", style, ref: dialogRef },
        h(ui.Panel, {
          className: "atrNativePanel",
        header: h("div", { className: "atrDialogHeader", onMouseDownCapture: beginDrag }, "Automatic Texture Replacer"),
          onClose: () => trigger("automatictexturereplacer", "closeDialog"),
        }, body), resizeCorners);
    }

    return h("div", { className: "atrDialogPositioner", style, ref: dialogRef },
      h("section", { className: "atrFallbackPanel" },
        h("header", { className: "atrDialogHeader", onMouseDown: beginDrag },
          h("span", null, "Automatic Texture Replacer"),
          h("button", { className: "atrCloseButton", onMouseDown: (e) => e.stopPropagation(), onClick: () => trigger("automatictexturereplacer", "closeDialog"), title: "Close" }, "×")),
      body), resizeCorners);
  };

  moduleRegistry.append("GameTopLeft", ToolbarButton);
  moduleRegistry.append("Game", Dialog);
  moduleRegistry.append("Game", SurfacePicker);
};

const hasCSS = true;
export { register as default, hasCSS };
