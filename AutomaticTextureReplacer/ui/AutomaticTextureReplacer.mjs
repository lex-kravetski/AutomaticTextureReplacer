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
  const BUTTON_STYLE = Object.freeze({
    standard: "standard",
    pickerGlobal: "pickerGlobal",
    scope: "scope",
  });
  const BUTTON_STYLE_DEFINITIONS = Object.freeze({
    [BUTTON_STYLE.standard]: ({ fontSize }) => ({
      outer: { margin: "8rem" },
      button: { fontSize, padding: "6rem 14rem" },
    }),
    [BUTTON_STYLE.pickerGlobal]: ({ fontSize, scale }) => ({
      outer: { margin: `${3 * scale}rem` },
      button: { fontSize, padding: `${6 * scale}rem ${14 * scale}rem` },
    }),
    [BUTTON_STYLE.scope]: ({ fontSize }) => ({
      outer: { margin: "2rem" },
      button: { fontSize, padding: "4rem 12rem" },
    }),
  });
  const createButton = (label, onSelect, options = {}) => {
    const {
      variant = "flat",
      title,
      className,
      outerClassName: customOuterClassName,
      buttonProps = {},
      fontSize,
      style = BUTTON_STYLE.standard,
      scale = 1,
    } = options;
    const buttonClassName = ["atrGameButton", className].filter(Boolean).join(" ");
    const outerClassName = ["atrButtonOuter", customOuterClassName].filter(Boolean).join(" ");
    const styleDefinition = BUTTON_STYLE_DEFINITIONS[style] || BUTTON_STYLE_DEFINITIONS[BUTTON_STYLE.standard];
    const resolvedStyle = styleDefinition({ fontSize, scale });
    const outerStyle = resolvedStyle.outer;
    const buttonStyle = resolvedStyle.button;
    const button = ui.Button
      ? h(ui.Button, {
        ...buttonProps,
        variant,
        focusKey: FocusDisabled,
        title,
        tooltipLabel: title,
        onSelect,
        className: buttonClassName,
        style: buttonStyle,
      }, label)
      : h("button", {
        ...buttonProps,
        type: "button",
        title,
        onClick: onSelect,
        className: buttonClassName,
        style: buttonStyle,
      }, label);
    return h("div", { className: outerClassName, style: outerStyle }, button);
  };
  const createTintedPrimaryButton = (label, onSelect, options = {}) => {
    const { tone = "blue", className, ...buttonOptions } = options;
    return createButton(label, onSelect, {
      ...buttonOptions,
      variant: "primary",
      className: ["atrTintedPrimaryButton", `atrTintedPrimaryButton-${tone}`, className].filter(Boolean).join(" "),
    });
  };
  const GameCheckbox = moduleRegistry.get("game-ui/common/input/toggle/checkbox/checkbox.tsx", "Checkbox");
  const FocusDisabled = moduleRegistry.get("game-ui/common/focus/focus-key.ts", "FOCUS_DISABLED");
  const surfaceCatalog$ = bindValue("automatictexturereplacer", "surfaceCatalog", "[]");
  const areaCatalog$ = bindValue("automatictexturereplacer", "areaCatalog", "[]");
  const surfacePickerOpen$ = bindValue("automatictexturereplacer", "surfacePickerOpen", false);
  const surfacePickerCollapsedGroups$ = bindValue("automatictexturereplacer", "surfacePickerCollapsedGroups", "{}");
  const surfacePickerX$ = bindValue("automatictexturereplacer", "surfacePickerX", -1);
  const surfacePickerY$ = bindValue("automatictexturereplacer", "surfacePickerY", -1);
  const surfacePickerWidth$ = bindValue("automatictexturereplacer", "surfacePickerWidth", 920);
  const surfacePickerHeight$ = bindValue("automatictexturereplacer", "surfacePickerHeight", 680);
  const surfacePickerContext$ = bindValue("automatictexturereplacer", "surfacePickerContext", "");
  const dialogFontSize$ = bindValue("automatictexturereplacer", "dialogFontSize", 100);
  const replacementScope$ = bindValue("automatictexturereplacer", "replacementScope", "assets");
  const activeMainTab$ = bindValue("automatictexturereplacer", "activeMainTab", "surfaces");
  const alsoNewBuildings$ = bindValue("automatictexturereplacer", "alsoNewBuildings", false);
  const showInGameSurfacesAsTargets$ = bindValue("automatictexturereplacer", "showInGameSurfacesAsTargets", false);
  const showSpecialIndustrySurfacesAsTargets$ = bindValue("automatictexturereplacer", "showSpecialIndustrySurfacesAsTargets", false);
  const showModSurfacesAsSources$ = bindValue("automatictexturereplacer", "showModSurfacesAsSources", false);
  const showRevertAllButton$ = bindValue("automatictexturereplacer", "showRevertAllButton", false);
  const rulesJson$ = bindValue("automatictexturereplacer", "rulesJson", "[]");
  const areaRulesJson$ = bindValue("automatictexturereplacer", "areaRulesJson", "[]");
  const makeSurfaceTooltip = (surface, includeGroup, scaledFontSize) => h("div", {
    className: "atrSurfaceTooltip",
    style: { fontSize: scaledFontSize },
  },
  h("strong", null, surface.name),
  includeGroup ? h("span", null, surface.group || "In-game") : null,
  h("span", null, surface.menuPath || "In-game asset"),
  h("img", { className: "atrSurfaceTooltipImage", src: surface.tooltipThumbnail || surface.thumbnail, alt: surface.name }));

  const RuleRow = React.memo(({ rule, ruleKind, deleteRule, setRuleEnabled, openSurfacePicker, surfaceCatalogById, areaCatalogById, scaledFontSize, rowIndex }) => {
    const lastPickerOpenAt = React.useRef(0);
    const openPickerOnce = (side) => {
      const now = Date.now();
      if (now - lastPickerOpenAt.current < 250) return;
      lastPickerOpenAt.current = now;
      openSurfacePicker(rule, side, ruleKind);
    };
    const checkbox = GameCheckbox
      ? h(GameCheckbox, { checked: rule.enabled, focusKey: FocusDisabled, className: "atrRuleCheckbox", style: { width: "38rem", height: "38rem" }, onChange: (enabled) => setRuleEnabled(rule.id, enabled) })
      : h("span", null, rule.enabled ? "✓" : "");
    const surfaces = (title, side) => {
      const sourceIsArea = ruleKind === "areas" && side === "source";
      const idsKey = side === "target" ? "targetSurfaceIds" : sourceIsArea ? "sourceAreaIds" : "sourceSurfaceIds";
      const catalogById = sourceIsArea ? areaCatalogById : surfaceCatalogById;
      const ids = rule[idsKey] || [];
      const icons = ids.map((id) => catalogById.get(id)).filter(Boolean).map((surface) => {
        const icon = h("img", {
          className: "atrRuleSurfaceIcon",
          src: surface.thumbnail,
          alt: surface.name,
        });
        const framedIcon = h("div", { className: "atrRuleSurfaceIconFrame" }, icon);
        return ui.Tooltip
          ? h(ui.Tooltip, { key: surface.id, tooltip: makeSurfaceTooltip(surface, true, scaledFontSize), delayTime: 0, hideOnInteraction: false }, framedIcon)
          : h("span", { key: surface.id, title: `${surface.name} — ${surface.group || "In-game"}` }, framedIcon);
      });
      const editButton = createButton("Edit", () => openPickerOnce(side), {
        variant: "primary",
        title,
        className: "atrEditButton",
        fontSize: scaledFontSize,
        style: BUTTON_STYLE.standard,
      });
      return h("div", {
        className: "atrSurfaceGroup atrRuleSurfaceCell",
        onMouseDownCapture: (event) => {
          if (event.button === 0 && event.target.closest(".atrEditButton")) openPickerOnce(side);
        },
      },
      h("div", { className: "atrRuleEditCell" }, editButton),
      h("div", { className: "atrRuleTileCell" }, h("div", { className: "atrRuleSurfaceIcons" }, icons)));
    };
    const actions = h("div", { className: "atrRuleActions" },
      createButton("Replace", () => trigger("automatictexturereplacer", ruleKind === "areas" ? "replaceAreas" : "replaceSurfaces", String(rule.id)), {
        variant: "primary",
        title: ruleKind === "areas" ? "Replace this industry area" : "Replace rule",
        className: "atrReplaceButton",
        fontSize: scaledFontSize,
        style: BUTTON_STYLE.standard,
      }),
      createButton("×", () => deleteRule(rule.id), {
        variant: "primary",
        title: "Delete rule",
        className: "atrDeleteRuleButton",
        fontSize: scaledFontSize,
        style: BUTTON_STYLE.standard,
      }));

    return h("div", {
      className: "atrRuleRow" + (rowIndex % 2 ? " atrRuleRowAlternate" : ""),
    },
    h("div", { className: "atrRuleLeftCell" }, checkbox, surfaces(ruleKind === "areas" ? "Edit source industries" : "Edit source surfaces", "source")),
    surfaces("Edit replacement surfaces", "target"),
    actions);
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

  const SurfaceThumbnailPreloader = () => {
    const catalogJson = useValue(surfaceCatalog$);
    const thumbnails = React.useMemo(() => {
      try {
        return JSON.parse(catalogJson || "[]")
          .map((surface) => surface.thumbnail)
          .filter((src) => src && !src.startsWith("thumbnail://"));
      } catch (error) {
        console.error("[AutomaticTextureReplacer] Invalid surface catalog while warming thumbnails", error);
        return [];
      }
    }, [catalogJson]);

    return h("div", { className: "atrSurfaceThumbnailPreloader", "aria-hidden": true },
      thumbnails.map((src, index) => h("img", { key: `${index}-${src}`, src, loading: "eager", alt: "" })));
  };

  const SurfacePicker = () => {
    const open = useValue(surfacePickerOpen$);
    const collapsedGroupsJson = useValue(surfacePickerCollapsedGroups$);
    const savedX = useValue(surfacePickerX$);
    const savedY = useValue(surfacePickerY$);
    const savedWidth = useValue(surfacePickerWidth$);
    const savedHeight = useValue(surfacePickerHeight$);
    const fontSize = useValue(dialogFontSize$);
    const showInGameSurfacesAsTargets = useValue(showInGameSurfacesAsTargets$);
    const showSpecialIndustrySurfacesAsTargets = useValue(showSpecialIndustrySurfacesAsTargets$);
    const showModSurfacesAsSources = useValue(showModSurfacesAsSources$);
    const catalogJson = useValue(surfaceCatalog$);
    const areaCatalogJson = useValue(areaCatalog$);
    const contextJson = useValue(surfacePickerContext$);
    const rulesJson = useValue(rulesJson$);
    const areaRulesJson = useValue(areaRulesJson$);
    const [selectedIds, setSelectedIds] = React.useState(() => new Set());
    const [collapsedGroups, setCollapsedGroups] = React.useState({});
    const lastSelection = React.useRef(null);
    const pendingShiftClick = React.useRef(false);
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
    const areaCatalog = React.useMemo(() => {
      try {
        return JSON.parse(areaCatalogJson || "[]");
      } catch (error) {
        console.error("[AutomaticTextureReplacer] Invalid special industry catalog", error);
        return [];
      }
    }, [areaCatalogJson]);
    const pickerContext = React.useMemo(() => {
      try {
        return JSON.parse(contextJson || "{}");
      } catch (error) {
        console.error("[AutomaticTextureReplacer] Invalid surface picker context", error);
        return {};
      }
    }, [contextJson]);
    const targetPicker = pickerContext.side === "target";
    const pickerCatalog = React.useMemo(() => {
      if (pickerContext.kind === "areas" && !targetPicker) return areaCatalog;
      if (targetPicker) {
        return catalog.filter((surface) => {
          if (surface.sourceOnly === true || surface.sourceOnly === "True") return false;
          const groupName = surface.group || "In-game asset";
          if (groupName === "In-game" && !showInGameSurfacesAsTargets) return selectedIds.has(surface.id);
          if (groupName === "Special Industries" && !showSpecialIndustrySurfacesAsTargets) return selectedIds.has(surface.id);
          return true;
        });
      }

      if (!showModSurfacesAsSources) {
        return catalog.filter((surface) =>
          (surface.isMod !== true && surface.isMod !== "True") || selectedIds.has(surface.id));
      }
      return catalog;
    }, [catalog, areaCatalog, pickerContext.kind, targetPicker, showInGameSurfacesAsTargets, showSpecialIndustrySurfacesAsTargets, showModSurfacesAsSources, selectedIds]);
    const groups = React.useMemo(() => {
      const grouped = new Map();
      pickerCatalog.forEach((surface) => {
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
    }, [pickerCatalog]);
    React.useEffect(() => {
      if (open) {
        // Keep selections that are currently filtered out. They remain part of the rule
        // and can be removed later by enabling their category in mod settings.
        setSelectedIds(new Set(pickerContext.selectedIds || []));
        try {
          setCollapsedGroups(JSON.parse(collapsedGroupsJson || "{}"));
        } catch (error) {
          console.error("[AutomaticTextureReplacer] Invalid collapsed groups state", error);
          setCollapsedGroups({});
        }
        lastSelection.current = null;
      }
    }, [open, pickerContext, collapsedGroupsJson]);

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
    const spacingScale = fontSize / 100;
    const button = (label, onSelect, variant = "flat", className, style = BUTTON_STYLE.standard) => {
      const options = { className, fontSize: scaledFontSize, style, scale: spacingScale };
      return variant === "primary"
        ? createButton(label, onSelect, { ...options, variant })
        : createTintedPrimaryButton(label, onSelect, options);
    };
    const closePicker = () => trigger("automatictexturereplacer", "closeSurfacePicker");
    const applySelection = () => {
      const selectedIdsArray = Array.from(selectedIds);
        const sideProperty = pickerContext.side === "target"
          ? "targetSurfaceIds"
          : pickerContext.kind === "areas" ? "sourceAreaIds" : "sourceSurfaceIds";
        const sourceJson = pickerContext.kind === "areas" ? areaRulesJson : rulesJson;
      try {
        const rules = JSON.parse(sourceJson || "[]");
        const updatedRules = rules.map((rule) => String(rule.id) === String(pickerContext.rowId)
          ? { ...rule, [sideProperty]: selectedIdsArray }
          : rule);
        trigger("automatictexturereplacer", pickerContext.kind === "areas" ? "saveAreaRules" : "saveRules", JSON.stringify(updatedRules));
      } catch (error) {
        console.error("[AutomaticTextureReplacer] Failed to apply surface selection", error);
      }
      closePicker();
    };
    const selectSurface = (group, index, surface, shiftKey) => {
      const anchor = lastSelection.current;
      if (shiftKey && anchor?.groupName === group.name) {
        const start = Math.min(anchor.index, index);
        const end = Math.max(anchor.index, index);
        setSelectedIds((current) => {
          const next = new Set(current);
          for (let i = start; i <= end; i++) {
            if (anchor.select) next.add(group.surfaces[i].id);
            else next.delete(group.surfaces[i].id);
          }
          return next;
        });
        return;
      }

      const shouldSelect = !selectedIds.has(surface.id);
      lastSelection.current = { groupName: group.name, index, select: shouldSelect };
      setSelectedIds((current) => {
        const next = new Set(current);
        if (shouldSelect) next.add(surface.id);
        else next.delete(surface.id);
        return next;
      });
    };
    const selectGroup = (group, shouldSelect) => {
      setSelectedIds((current) => {
        const next = new Set(current);
        group.surfaces.forEach((surface) => shouldSelect ? next.add(surface.id) : next.delete(surface.id));
        return next;
      });
      if (group.surfaces.length) {
        lastSelection.current = { groupName: group.name, index: group.surfaces.length - 1, select: shouldSelect };
      } else if (lastSelection.current?.groupName === group.name) {
        lastSelection.current = null;
      }
    };
    const setAllGroupsCollapsed = (collapsed) => {
      const next = {};
      groups.forEach((group) => { next[group.name] = collapsed; });
      setCollapsedGroups(next);
      trigger("automatictexturereplacer", "saveSurfacePickerCollapsedGroups", JSON.stringify(next));
    };
    const renderTile = (group, surface, index) => {
      const selected = selectedIds.has(surface.id);
      const tooltip = makeSurfaceTooltip(surface, false, scaledFontSize);
      const tileContents = h("img", { src: surface.thumbnail, alt: surface.name });
      const handleSelect = () => selectSurface(group, index, surface, pendingShiftClick.current);
      const tileClassName = "atrSurfaceTile" + (selected ? " atrSurfaceTileSelected" : "");
      const tile = ui.Button
        ? h(ui.Button, {
          variant: "flat",
          className: tileClassName,
          focusKey: FocusDisabled,
          onMouseDown: (event) => { pendingShiftClick.current = event.shiftKey; },
          onSelect: handleSelect,
          "aria-label": surface.name,
        }, tileContents)
        : h("button", {
          className: tileClassName,
          onMouseDown: (event) => { pendingShiftClick.current = event.shiftKey; },
          onClick: handleSelect,
          title: surface.name,
        },
          tileContents);
      return ui.Tooltip
        ? h(ui.Tooltip, { key: surface.id, tooltip, delayTime: 0, hideOnInteraction: false }, tile)
        : h("div", { key: surface.id, title: surface.name }, tile);
    };
    const sections = groups.map((group) => {
      const collapsed = !!collapsedGroups[group.name];
      const toggle = () => {
        const next = { ...collapsedGroups, [group.name]: !collapsedGroups[group.name] };
        setCollapsedGroups(next);
        trigger("automatictexturereplacer", "saveSurfacePickerCollapsedGroups", JSON.stringify(next));
      };
      const groupToggle = h("div", {
        className: "atrPickerGroupToggle",
        onClick: toggle,
      },
        h("span", { className: "atrPickerGroupArrow", "aria-hidden": true }, collapsed ? "▲" : "▼"),
        h("strong", null, group.name),
        h("span", { className: "atrPickerGroupCount" }, String(group.surfaces.length)));
      const groupButtons = h("div", { className: "atrPickerGroupActions" },
        button("Select all", () => selectGroup(group, true), "flat"),
        button("Deselect all", () => selectGroup(group, false), "flat"));
      const header = h("div", { className: "atrPickerGroupHeader" }, groupToggle, groupButtons);
      return h("section", { key: group.name, className: "atrPickerGroupSection" },
        header,
        collapsed ? null : h("div", { className: "atrSurfaceGrid" }, group.surfaces.map((surface, index) => renderTile(group, surface, index))));
    });

    const style = {
      left: Math.max(0, Math.min(position.x, window.innerWidth - 100)) + "px",
      top: Math.max(0, Math.min(position.y, window.innerHeight - 80)) + "px",
      width: dimensions.width + "px",
      height: dimensions.height + "px",
    };
    const groupControls = h("div", {
      className: "atrPickerGlobalActions",
      style: { gap: `${8 * spacingScale}rem` },
    },
    button("Expand all", () => setAllGroupsCollapsed(false), "flat", "atrPickerGlobalButton", BUTTON_STYLE.pickerGlobal),
    button("Collapse all", () => setAllGroupsCollapsed(true), "flat", "atrPickerGlobalButton", BUTTON_STYLE.pickerGlobal),
    button("Deselect all", () => {
      setSelectedIds((current) => {
        const next = new Set(current);
        pickerCatalog.forEach((surface) => next.delete(surface.id));
        return next;
      });
      lastSelection.current = null;
    }, "flat", "atrPickerGlobalButton", BUTTON_STYLE.pickerGlobal));
    const footer = h("div", { className: "atrPickerFooter" },
      button("Apply", applySelection, "primary"),
      button("Cancel", closePicker, "flat"));
    const body = h("div", { className: "atrPickerBody", style: { fontSize: scaledFontSize } },
      groupControls,
      pickerCatalog.length === 0
        ? h("div", { className: "atrPickerEmpty" }, pickerContext.kind === "areas" && pickerContext.side === "source" ? "No special industries found." : "No surfaces found.")
        : h(ui.Scrollable || "div", { className: "atrPickerScroll", vertical: true, useNewStyle: true },
          h("div", { className: "atrPickerGroups" }, sections)),
      footer);
    const resizeCorners = h(React.Fragment, null,
      h("div", { className: "atrPickerResizeCorner atrResizeLeft", onMouseDown: (event) => beginResize(event, true), title: "Drag this corner to resize" }),
      h("div", { className: "atrPickerResizeCorner atrResizeRight", onMouseDown: (event) => beginResize(event), title: "Drag this corner to resize" }));

    return h("div", { className: "atrDialogPositioner atrPickerPositioner", style: { ...style, display: open ? "block" : "none" }, ref: dialogRef, "aria-hidden": !open },
      h(ui.Panel, {
        className: "atrPickerPanel",
        contentClassName: "atrPickerPanelContent",
      header: h("div", { className: "atrDialogHeader atrPickerHeader", onMouseDownCapture: beginDrag },
        pickerContext.kind === "areas" && pickerContext.side === "source" ? "Select a special industry" : "Select a surface"),
        onClose: closePicker,
      }, body), resizeCorners);
  };

  const Dialog = () => {
    const open = useValue(dialogOpen$);
    const savedX = useValue(dialogX$);
    const savedY = useValue(dialogY$);
    const savedWidth = useValue(dialogWidth$);
    const savedHeight = useValue(dialogHeight$);
    const fontSize = useValue(dialogFontSize$);
    const showRevertAllButton = useValue(showRevertAllButton$);
    const replacementScope = useValue(replacementScope$);
    const savedActiveTab = useValue(activeMainTab$);
    const alsoNewBuildings = useValue(alsoNewBuildings$);
    const rulesJson = useValue(rulesJson$);
    const areaRulesJson = useValue(areaRulesJson$);
    const catalogJson = useValue(surfaceCatalog$);
    const areaCatalogJson = useValue(areaCatalog$);
    const dialogRef = React.useRef(null);
    const rulesScrollRef = React.useRef(null);
    const nextRuleId = React.useRef(1);
    const nextAreaRuleId = React.useRef(1);
    const rulesRef = React.useRef([]);
    const areaRulesRef = React.useRef([]);
    const [dragPosition, setDragPosition] = React.useState(null);
    const [size, setSize] = React.useState(null);
    const [rules, setRules] = React.useState([]);
    const [areaRules, setAreaRules] = React.useState([]);
    const [activeTab, setActiveTab] = React.useState(savedActiveTab === "areas" ? "areas" : "surfaces");
    React.useEffect(() => {
      setActiveTab(savedActiveTab === "areas" ? "areas" : "surfaces");
    }, [savedActiveTab]);
    const catalog = React.useMemo(() => {
      try {
        return JSON.parse(catalogJson || "[]");
      } catch (error) {
        console.error("[AutomaticTextureReplacer] Invalid surface catalog", error);
        return [];
      }
    }, [catalogJson]);
    const catalogById = React.useMemo(() => new Map(catalog.map((surface) => [surface.id, surface])), [catalog]);
    const areaCatalog = React.useMemo(() => {
      try {
        return JSON.parse(areaCatalogJson || "[]");
      } catch (error) {
        console.error("[AutomaticTextureReplacer] Invalid special industry catalog", error);
        return [];
      }
    }, [areaCatalogJson]);
    const areaCatalogById = React.useMemo(() => new Map(areaCatalog.map((industry) => [industry.id, industry])), [areaCatalog]);
    React.useEffect(() => {
      try {
        const loaded = JSON.parse(rulesJson || "[]");
        const normalized = Array.isArray(loaded) ? loaded.map((rule, index) => ({
          id: rule.id ?? index + 1,
          enabled: rule.enabled !== false,
          sourceSurfaceIds: Array.isArray(rule.sourceSurfaceIds) ? rule.sourceSurfaceIds : [],
          targetSurfaceIds: Array.isArray(rule.targetSurfaceIds) ? rule.targetSurfaceIds : [],
        })) : [];
        rulesRef.current = normalized;
        setRules(normalized);
        const highestNumericId = normalized.reduce((highest, rule) =>
          Number.isFinite(Number(rule.id)) ? Math.max(highest, Number(rule.id)) : highest, 0);
        nextRuleId.current = Math.max(nextRuleId.current, highestNumericId + 1);
      } catch (error) {
        console.error("[AutomaticTextureReplacer] Invalid saved rules", error);
        rulesRef.current = [];
        setRules([]);
      }
    }, [rulesJson]);
    React.useEffect(() => {
      try {
        const loaded = JSON.parse(areaRulesJson || "[]");
        const normalized = Array.isArray(loaded) ? loaded.map((rule, index) => ({
          id: rule.id ?? index + 1,
          enabled: rule.enabled !== false,
          sourceAreaIds: Array.isArray(rule.sourceAreaIds) ? rule.sourceAreaIds : [],
          targetSurfaceIds: Array.isArray(rule.targetSurfaceIds) ? rule.targetSurfaceIds : [],
        })) : [];
        areaRulesRef.current = normalized;
        setAreaRules(normalized);
        const highestNumericId = normalized.reduce((highest, rule) =>
          Number.isFinite(Number(rule.id)) ? Math.max(highest, Number(rule.id)) : highest, 0);
        nextAreaRuleId.current = Math.max(nextAreaRuleId.current, highestNumericId + 1);
      } catch (error) {
        console.error("[AutomaticTextureReplacer] Invalid saved area rules", error);
        areaRulesRef.current = [];
        setAreaRules([]);
      }
    }, [areaRulesJson]);
    const persistRules = React.useCallback((nextRules) => {
      rulesRef.current = nextRules;
      setRules(nextRules);
      trigger("automatictexturereplacer", "saveRules", JSON.stringify(nextRules));
    }, []);
    const persistAreaRules = React.useCallback((nextRules) => {
      areaRulesRef.current = nextRules;
      setAreaRules(nextRules);
      trigger("automatictexturereplacer", "saveAreaRules", JSON.stringify(nextRules));
    }, []);
    const deleteRule = React.useCallback((id, kind) => {
      if (kind === "areas") {
        persistAreaRules(areaRulesRef.current.filter((item) => String(item.id) !== String(id)));
      } else {
        persistRules(rulesRef.current.filter((item) => String(item.id) !== String(id)));
      }
    }, [persistAreaRules, persistRules]);
    const setRuleEnabled = React.useCallback((id, enabled, kind) => {
      if (kind === "areas") {
        persistAreaRules(areaRulesRef.current.map((item) => String(item.id) === String(id) ? { ...item, enabled } : item));
      } else {
        persistRules(rulesRef.current.map((item) => String(item.id) === String(id) ? { ...item, enabled } : item));
      }
    }, [persistAreaRules, persistRules]);
    const openSurfacePicker = React.useCallback((rule, side, kind) => {
      const selectedIds = side === "target"
        ? rule.targetSurfaceIds
        : kind === "areas" ? rule.sourceAreaIds : rule.sourceSurfaceIds;
      trigger("automatictexturereplacer", "openSurfacePicker", JSON.stringify({
        rowId: rule.id,
        side,
        kind,
        selectedIds: selectedIds || [],
      }));
    }, []);

    if (!open) return null;

    const minimumDialogWidth = Math.round(760 * fontSize / 100);
    const initialWidth = Math.max(minimumDialogWidth, savedWidth);
    const initialX = savedX < 0 ? Math.max(0, Math.round((window.innerWidth - initialWidth) / 2)) : savedX;
    const initialY = savedY < 0 ? Math.max(0, Math.round((window.innerHeight - savedHeight) / 2)) : savedY;
    const position = dragPosition || { x: initialX, y: initialY };
    const dimensions = { width: Math.max(minimumDialogWidth, size?.width ?? savedWidth), height: size?.height ?? savedHeight };

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
        const width = Math.max(minimumDialogWidth, startWidth + horizontalDelta(e));
        setSize({ width, height: Math.max(200, startHeight + e.clientY - startY) });
        if (fromLeft) setDragPosition((current) => ({ ...(current || position), x: startLeft + startWidth - width }));
      };
      const end = (e) => {
        window.removeEventListener("mousemove", move);
        window.removeEventListener("mouseup", end);
        const width = Math.max(minimumDialogWidth, Math.min(1600, Math.round(startWidth + horizontalDelta(e))));
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
      const isAreas = activeTab === "areas";
      const newRule = {
        id: isAreas ? nextAreaRuleId.current++ : nextRuleId.current++,
        enabled: true,
        ...(isAreas ? { sourceAreaIds: [] } : { sourceSurfaceIds: [] }),
        targetSurfaceIds: [],
      };
      const nextRules = isAreas ? [...areaRulesRef.current, newRule] : [...rulesRef.current, newRule];
      if (isAreas) persistAreaRules(nextRules);
      else persistRules(nextRules);
      window.requestAnimationFrame(() => {
        const scrollArea = rulesScrollRef.current;
        if (scrollArea) scrollArea.scrollTop = scrollArea.scrollHeight;
      });
    };

    const activeRules = activeTab === "areas" ? areaRules : rules;
    const rows = activeRules.map((rule, index) => h(RuleRow, {
      key: `${activeTab}-${rule.id}`,
      rule,
      ruleKind: activeTab,
      rowIndex: index,
      deleteRule: (id) => deleteRule(id, activeTab),
      setRuleEnabled: (id, enabled) => setRuleEnabled(id, enabled, activeTab),
      openSurfacePicker,
      surfaceCatalogById: catalogById,
      areaCatalogById,
      scaledFontSize: Math.round(13 * fontSize) / 100 + "rem",
    }));

    const style = {
      left: Math.max(0, Math.min(position.x, window.innerWidth - 80)) + "px",
      top: Math.max(0, Math.min(position.y, window.innerHeight - 50)) + "px",
      width: dimensions.width + "px",
      height: dimensions.height + "px",
      minWidth: minimumDialogWidth + "px",
    };

    const scaledFontSize = Math.round(13 * fontSize) / 100 + "rem";
    const scopeOptions = [
      ["assets", "Assets"],
      ["hand-drawn-surfaces", "Hand-drawn surfaces"],
      ["all", "All"],
    ];
    const scopeOptionsGroup = h("div", {
      className: "atrReplacementScopeGroup",
      role: "radiogroup",
      "aria-label": "Surface replacement scope",
    }, scopeOptions.map(([value, label]) => {
      const selected = replacementScope === value;
      return createButton(label, () => trigger("automatictexturereplacer", "setReplacementScope", value), {
        variant: "primary",
        className: "atrReplacementScopeOption" + (selected ? " atrReplacementScopeOptionSelected" : ""),
        buttonProps: { role: "radio", "aria-checked": selected, tabIndex: selected ? 0 : -1 },
        fontSize: scaledFontSize,
        style: BUTTON_STYLE.scope,
      });
    }));
    const alsoNewBuildingsCheckbox = GameCheckbox
      ? h(GameCheckbox, {
        checked: alsoNewBuildings,
        focusKey: FocusDisabled,
        className: "atrAlsoNewBuildingsCheckbox",
        onChange: (checked) => trigger("automatictexturereplacer", "setAlsoNewBuildings", checked),
      })
      : h("input", {
        type: "checkbox",
        checked: alsoNewBuildings,
        onChange: (event) => trigger("automatictexturereplacer", "setAlsoNewBuildings", event.target.checked),
      });
    const alsoNewBuildingsControl = h("div", { className: "atrAlsoNewBuildingsControl" },
      h("span", null, "Also new buildings"),
      alsoNewBuildingsCheckbox);
    const alsoNewBuildingsTooltip = ui.Tooltip
      ? h(ui.Tooltip, {
        tooltip: "Automatic replacement applies only to rules with the checkbox enabled.",
        delayTime: 0,
        hideOnInteraction: false,
      }, alsoNewBuildingsControl)
      : alsoNewBuildingsControl;
    const scopeSelector = h("div", { className: "atrScopeToolbar" + (activeTab === "areas" ? " atrScopeToolbarAreas" : "") },
      activeTab === "surfaces" ? scopeOptionsGroup : null,
      alsoNewBuildingsTooltip);
    const tabs = h("div", { className: "atrMainTabs", role: "tablist", "aria-label": "Replacement category" },
      [["surfaces", "Surfaces"], ["areas", "Areas"]].map(([tab, label]) => createButton(label, () => {
        setActiveTab(tab);
        trigger("automatictexturereplacer", "setActiveMainTab", tab);
      }, {
        variant: "primary",
        className: "atrMainTab" + (activeTab === tab ? " atrMainTabSelected" : ""),
        buttonProps: { role: "tab", "aria-selected": activeTab === tab },
        fontSize: scaledFontSize,
        style: BUTTON_STYLE.standard,
      })));
    const replacementTrigger = activeTab === "areas" ? "replaceAreas" : "replaceSurfaces";
    const revertTrigger = activeTab === "areas" ? "revertAllAreas" : "revertAllSurfaces";
    const body = h("div", { className: "atrDialogBody", style: { fontSize: scaledFontSize } },
      h("div", { className: "atrActiveArea" },
      tabs,
      h("div", { className: "atrRulesToolbar" },
        h("div", { className: "atrRulesToolbarLeft" },
          createButton("Replace All", () => trigger("automatictexturereplacer", replacementTrigger, ""), {
            variant: "primary",
            fontSize: scaledFontSize,
            style: BUTTON_STYLE.standard,
          }),
          showRevertAllButton && createButton("Revert All", () => trigger("automatictexturereplacer", revertTrigger), {
            variant: "primary",
            className: "atrRevertAllButton",
            fontSize: scaledFontSize,
            style: BUTTON_STYLE.standard,
          })),
        createButton("Add", addRule, {
          variant: "primary",
          outerClassName: "atrAddRuleButton",
          fontSize: scaledFontSize,
          style: BUTTON_STYLE.standard,
        })),
      scopeSelector,
      h(ui.PanelSectionRow, {
        disableFocus: true,
        className: "atrRulesHeader",
        left: h("div", { className: "atrHeaderSource" },
          h("span", { className: "atrCheckboxSpacer" }),
          h("strong", { style: { fontSize: scaledFontSize } }, activeTab === "areas" ? "Special industries to replace" : "Surfaces to replace")),
        center: h("strong", { style: { fontSize: scaledFontSize } }, "Replacement surfaces"),
        right: null,
      }),
      h(ui.Scrollable || "div", {
        className: "atrRulesScroll",
        ref: rulesScrollRef,
        vertical: true,
        horizontal: false,
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
  moduleRegistry.append("Game", SurfaceThumbnailPreloader);
};

const hasCSS = true;
export { register as default, hasCSS };
