import React, { useEffect, useMemo, useRef, useState } from 'react'
import { flushSync } from 'react-dom'
import { Tree } from 'react-arborist'
import samplePhaseLayout from '../phase_ui_layout.sample.json'

const defaultPanelTemplate = {
  panelType: 'PhaseParasPanel',
  title: 'Phase Parameters Area',
  rowLayoutPath: 'VL/HL',
  rowLayoutHorizontalGap: 8,
  sectionVerticalGap: 8,
  items: [],
}

const defaultDocumentTemplate = {
  version: 3,
  description: 'Single-template test layout aligned to UDT_Phase. Two test configs per area.',
  sections: [],
}

const childWidgetPresets = [
  {
    widgetType: 'PhaseSinglePara',
    label: 'Para',
    bind: {
      uiProperty: 'Text',
      sourceTagPath: 'PP.FixedSetPointValue[0]',
    },
  },
  {
    widgetType: 'PhaseValvePanel',
    label: 'Valve',
    bind: {
      uiProperty: 'Checked',
      sourceTagPath: 'PP.Valve[0]',
    },
  },
]

const panelEndConditionOptionPresets = [
  { label: 'Time', value: '1', bindingSlots: ['timeHr', 'timeMin', 'timeSec'] },
  { label: 'Weight', value: '2', bindingSlots: ['weightValue', 'weightOperator'] },
  { label: 'pH', value: '3', bindingSlots: ['phValue', 'phOperator'] },
  { label: 'Oxygen', value: '4', bindingSlots: ['oxygenValue', 'oxygenOperator'] },
  { label: 'Temp', value: '5', bindingSlots: ['tempValue', 'tempOperator'] },
]

function createOptionBindings(slotKeys, sourceTagPaths = {}) {
  return Object.fromEntries(
    slotKeys.map((slotKey) => [
      slotKey,
      {
        sourceTagPath: typeof sourceTagPaths[slotKey] === 'string' ? sourceTagPaths[slotKey] : '',
      },
    ]),
  )
}

const compositeWidgetSchemas = {
  PanelEndConditionGroup: {
    dropdownSlotKey: 'conditionSelector',
    fixedSlotKeys: [
      'enableSwitch',
      'conditionSelector',
    ],
    slotOrder: [
      'enableSwitch',
      'conditionSelector',
      'timeHr',
      'timeMin',
      'timeSec',
      'weightValue',
      'weightOperator',
      'phValue',
      'phOperator',
      'oxygenValue',
      'oxygenOperator',
      'tempValue',
      'tempOperator',
    ],
    slots: {
      enableSwitch: {
        label: 'Enable Switch',
        uiPath: 'VerticalLayout1/PanelEndConditionsEnable1/Rectangle_Border/HorizontalLayout1/Switch_Vlv1',
        uiProperty: 'Checked',
      },
      conditionSelector: {
        label: 'Condition Selector',
        uiPath: 'VerticalLayout1/PanelEndConditionSelection1/Rectangle_Border/Panel2/ComboBox1',
        uiProperty: 'SelectedValue',
      },
      timeHr: {
        label: 'Time Hour',
        uiPath: 'VerticalLayout1/PanelEndConditionSelection1/Rectangle_Border/Panel2/TimeContainer/SpinBox_Hr',
        uiProperty: 'Value',
      },
      timeMin: {
        label: 'Time Minute',
        uiPath: 'VerticalLayout1/PanelEndConditionSelection1/Rectangle_Border/Panel2/TimeContainer/SpinBox_Min',
        uiProperty: 'Value',
      },
      timeSec: {
        label: 'Time Second',
        uiPath: 'VerticalLayout1/PanelEndConditionSelection1/Rectangle_Border/Panel2/TimeContainer/SpinBox_Sec',
        uiProperty: 'Value',
      },
      weightValue: {
        label: 'Weight Value',
        uiPath: 'VerticalLayout1/PanelEndConditionSelection1/Rectangle_Border/Panel2/WeightContainer/SpinBox_Weight',
        uiProperty: 'Value',
      },
      weightOperator: {
        label: 'Weight Operator',
        uiPath: 'VerticalLayout1/PanelEndConditionSelection1/Rectangle_Border/Panel2/WeightContainer/Switch_Operator',
        uiProperty: 'Checked',
      },
      phValue: {
        label: 'pH Value',
        uiPath: 'VerticalLayout1/PanelEndConditionSelection1/Rectangle_Border/Panel2/PHContainer/SpinBox_pH',
        uiProperty: 'Value',
      },
      phOperator: {
        label: 'pH Operator',
        uiPath: 'VerticalLayout1/PanelEndConditionSelection1/Rectangle_Border/Panel2/PHContainer/Switch_Operator',
        uiProperty: 'Checked',
      },
      oxygenValue: {
        label: 'Oxygen Value',
        uiPath: 'VerticalLayout1/PanelEndConditionSelection1/Rectangle_Border/Panel2/OxygenContainer/SpinBox_Oxygen',
        uiProperty: 'Value',
      },
      oxygenOperator: {
        label: 'Oxygen Operator',
        uiPath: 'VerticalLayout1/PanelEndConditionSelection1/Rectangle_Border/Panel2/OxygenContainer/Switch_Operator',
        uiProperty: 'Checked',
      },
      tempValue: {
        label: 'Temperature Value',
        uiPath: 'VerticalLayout1/PanelEndConditionSelection1/Rectangle_Border/Panel2/TempContainer/SpinBox_Temp',
        uiProperty: 'Value',
      },
      tempOperator: {
        label: 'Temperature Operator',
        uiPath: 'VerticalLayout1/PanelEndConditionSelection1/Rectangle_Border/Panel2/TempContainer/Switch_Operator',
        uiProperty: 'Checked',
      },
    },
    optionBindingMap: {
      '1': ['timeHr', 'timeMin', 'timeSec'],
      '2': ['weightValue', 'weightOperator'],
      '3': ['phValue', 'phOperator'],
      '4': ['oxygenValue', 'oxygenOperator'],
      '5': ['tempValue', 'tempOperator'],
    },
  },
}

function getCompositeWidgetSchema(widgetType) {
  return compositeWidgetSchemas[widgetType] ?? null
}

function isCompositeWidget(itemOrWidgetType) {
  const widgetType = typeof itemOrWidgetType === 'string' ? itemOrWidgetType : itemOrWidgetType?.widgetType
  return getCompositeWidgetSchema(widgetType) !== null
}

function widgetTypeRequiresLabel(widgetType) {
  return widgetType !== 'PanelEndConditionsOperator'
}

function createConditionSelectorConfig(items = panelEndConditionOptionPresets) {
  return {
    items: items.map((item) => ({
      label: item.label,
      value: item.value,
      bindings: createOptionBindings(Array.isArray(item.bindingSlots) ? item.bindingSlots : []),
    })),
  }
}

function getPresetConditionOption(value) {
  return panelEndConditionOptionPresets.find((item) => item.value === value) ?? null
}

function getConditionOptionBindings(schema, option) {
  if (option?.bindings && typeof option.bindings === 'object' && !Array.isArray(option.bindings)) {
    const slotKeys = Object.keys(option.bindings).filter((slotKey) => schema?.slots?.[slotKey])

    return slotKeys.map((slotKey) => ({
      slotKey,
      sourceTagPath: typeof option.bindings[slotKey]?.sourceTagPath === 'string' ? option.bindings[slotKey].sourceTagPath : '',
    }))
  }

  if (Array.isArray(option?.bindingSlots) && option.bindingSlots.length > 0) {
    return option.bindingSlots
      .filter((slotKey) => typeof slotKey === 'string' && schema?.slots?.[slotKey])
      .map((slotKey) => ({ slotKey, sourceTagPath: '' }))
  }

  return Array.isArray(schema?.optionBindingMap?.[option?.value])
    ? schema.optionBindingMap[option.value].map((slotKey) => ({ slotKey, sourceTagPath: '' }))
    : []
}

function getCompositeSlotBindings(item) {
  const schema = getCompositeWidgetSchema(item.widgetType)
  if (!schema) {
    return []
  }

  return schema.slotOrder.map((slotKey) => {
    const slot = schema.slots[slotKey]
    const slotState = item.slots?.[slotKey]

    return {
      slotKey,
      label: slot.label,
      uiPath: slot.uiPath,
      uiProperty: slot.uiProperty,
      sourceTagPath: typeof slotState?.sourceTagPath === 'string' ? slotState.sourceTagPath : '',
    }
  })
}

function getCompositeSlotBinding(item, slotKey) {
  const schema = getCompositeWidgetSchema(item.widgetType)
  const slot = schema?.slots?.[slotKey]

  if (!slot) {
    return null
  }

  const slotState = item.slots?.[slotKey]

  return {
    slotKey,
    label: slot.label,
    uiPath: slot.uiPath,
    uiProperty: slot.uiProperty,
    sourceTagPath: typeof slotState?.sourceTagPath === 'string' ? slotState.sourceTagPath : '',
  }
}

function getCompositeFixedBindings(item) {
  const schema = getCompositeWidgetSchema(item.widgetType)
  if (!schema) {
    return []
  }

  return (schema.fixedSlotKeys ?? []).map((slotKey) => getCompositeSlotBinding(item, slotKey)).filter(Boolean)
}

function getCompositeOptionBindingGroups(item) {
  const schema = getCompositeWidgetSchema(item.widgetType)
  if (!schema) {
    return []
  }

  return getDropdownItems(item, schema.dropdownSlotKey).map((option, optionIndex) => {
    const optionBindings = getConditionOptionBindings(schema, option)
    const slotKeys = optionBindings.map(({ slotKey }) => slotKey)

    return {
      option,
      optionIndex,
      slotKeys,
      bindings: optionBindings.map(({ slotKey, sourceTagPath }) => {
        const slotBinding = getCompositeSlotBinding(item, slotKey)
        return slotBinding
          ? {
              ...slotBinding,
              sourceTagPath,
            }
          : null
      }).filter(Boolean),
    }
  })
}

function getDropdownItems(item, slotKey = 'conditionSelector') {
  const items = item.config?.[slotKey]?.items

  if (!Array.isArray(items)) {
    return []
  }

  return items.filter((entry) => entry && typeof entry === 'object' && !Array.isArray(entry))
}

function getItemBindingCount(item) {
  if (isCompositeWidget(item)) {
    const fixedBindings = getCompositeFixedBindings(item).length
    const optionBindings = getCompositeOptionBindingGroups(item).reduce((count, group) => count + group.bindings.length, 0)
    return fixedBindings + optionBindings
  }

  return getItemBindings(item).length
}

function updateCompositeSlotBinding(item, slotKey, sourceTagPath) {
  return {
    ...item,
    slots: {
      ...(item.slots && typeof item.slots === 'object' && !Array.isArray(item.slots) ? item.slots : {}),
      [slotKey]: {
        sourceTagPath,
      },
    },
  }
}

function updateConditionOptionBinding(item, slotKey, optionIndex, bindingSlotKey, sourceTagPath) {
  const nextItems = getDropdownItems(item, slotKey).map((entry, index) => {
    if (index !== optionIndex) {
      return entry
    }

    const currentBindings = entry?.bindings && typeof entry.bindings === 'object' && !Array.isArray(entry.bindings)
      ? entry.bindings
      : {}

    return {
      ...entry,
      bindings: {
        ...currentBindings,
        [bindingSlotKey]: {
          sourceTagPath,
        },
      },
    }
  })

  return {
    ...item,
    config: {
      ...(item.config && typeof item.config === 'object' && !Array.isArray(item.config) ? item.config : {}),
      [slotKey]: {
        items: nextItems,
      },
    },
  }
}

function addConditionOptionBinding(item, slotKey, optionIndex, bindingSlotKey) {
  return updateConditionOptionBinding(item, slotKey, optionIndex, bindingSlotKey, '')
}

function removeConditionOptionBinding(item, slotKey, optionIndex, bindingSlotKey) {
  const nextItems = getDropdownItems(item, slotKey).map((entry, index) => {
    if (index !== optionIndex) {
      return entry
    }

    if (!entry?.bindings || typeof entry.bindings !== 'object' || Array.isArray(entry.bindings)) {
      return entry
    }

    const nextBindings = { ...entry.bindings }
    delete nextBindings[bindingSlotKey]

    return {
      ...entry,
      bindings: nextBindings,
    }
  })

  return {
    ...item,
    config: {
      ...(item.config && typeof item.config === 'object' && !Array.isArray(item.config) ? item.config : {}),
      [slotKey]: {
        items: nextItems,
      },
    },
  }
}

function getAvailableConditionOptionSlotKeys(item, option) {
  const schema = getCompositeWidgetSchema(item.widgetType)
  if (!schema) {
    return []
  }

  const usedSlotKeys = new Set(getConditionOptionBindings(schema, option).map(({ slotKey }) => slotKey))
  const fixedSlotKeys = new Set(schema.fixedSlotKeys ?? [])

  return schema.slotOrder.filter((slotKey) => !fixedSlotKeys.has(slotKey) && !usedSlotKeys.has(slotKey))
}

function updateDropdownItem(item, slotKey, optionIndex, field, value) {
  const nextItems = getDropdownItems(item, slotKey).map((entry, index) => (
    index === optionIndex
      ? (() => {
          const nextEntry = {
            ...entry,
            [field]: value,
          }

          if (field === 'value') {
            const preset = getPresetConditionOption(value)
            nextEntry.bindings = createOptionBindings(preset ? preset.bindingSlots : [])
          }

          return nextEntry
        })()
      : entry
  ))

  return {
    ...item,
    config: {
      ...(item.config && typeof item.config === 'object' && !Array.isArray(item.config) ? item.config : {}),
      [slotKey]: {
        items: nextItems,
      },
    },
  }
}

function addDropdownItem(item, slotKey) {
  const nextItems = [...getDropdownItems(item, slotKey), { label: '', value: '', bindings: {} }]

  return {
    ...item,
    config: {
      ...(item.config && typeof item.config === 'object' && !Array.isArray(item.config) ? item.config : {}),
      [slotKey]: {
        items: nextItems,
      },
    },
  }
}

function removeDropdownItem(item, slotKey, optionIndex) {
  const nextItems = getDropdownItems(item, slotKey).filter((_, index) => index !== optionIndex)

  return {
    ...item,
    config: {
      ...(item.config && typeof item.config === 'object' && !Array.isArray(item.config) ? item.config : {}),
      [slotKey]: {
        items: nextItems,
      },
    },
  }
}

function moveDropdownItem(item, slotKey, fromIndex, toIndex) {
  return {
    ...item,
    config: {
      ...(item.config && typeof item.config === 'object' && !Array.isArray(item.config) ? item.config : {}),
      [slotKey]: {
        items: moveArrayItem(getDropdownItems(item, slotKey), fromIndex, toIndex),
      },
    },
  }
}

function createPanel(index) {
  return {
    id: `ParaPanel${index + 1}`,
    ...structuredClone(defaultPanelTemplate),
    title: index === 0 ? defaultPanelTemplate.title : `Panel ${index + 1}`,
  }
}

function createDefaultDocument() {
  return {
    ...structuredClone(defaultDocumentTemplate),
    sections: [createPanel(0)],
  }
}

function getNextChildIndex(document) {
  let maxIndex = -1

  for (const panel of document.sections) {
    for (const item of panel.items) {
      const match = /^Para(\d+)$/.exec(item.id)
      if (match) {
        maxIndex = Math.max(maxIndex, Number(match[1]))
      }
    }
  }

  return maxIndex + 1
}

function createChildItem(document) {
  const index = getNextChildIndex(document)
  const preset = childWidgetPresets[index % childWidgetPresets.length]

  return {
    id: `Para${index}`,
    widgetType: preset.widgetType,
    label: `${preset.label}${index + 1}`,
    bind: {
      uiProperty: preset.bind.uiProperty,
      sourceTagPath: preset.bind.sourceTagPath,
    },
  }
}

function isBindingObject(binding) {
  return binding && typeof binding === 'object' && !Array.isArray(binding)
}

function getItemBindings(item) {
  if (isCompositeWidget(item)) {
    return getCompositeSlotBindings(item)
  }

  if (Array.isArray(item.binds)) {
    return item.binds.filter(isBindingObject)
  }

  if (isBindingObject(item.bind)) {
    return [item.bind]
  }

  return []
}

function createDefaultBinding() {
  return {
    uiProperty: 'Text',
    sourceTagPath: '',
  }
}

function updateItemBinding(item, bindingIndex, field, value) {
  if (Array.isArray(item.binds)) {
    return {
      ...item,
      binds: item.binds.map((binding, index) => (
        index === bindingIndex
          ? {
              ...binding,
              [field]: value,
            }
          : binding
      )),
    }
  }

  return {
    ...item,
    bind: {
      ...(isBindingObject(item.bind) ? item.bind : {}),
      [field]: value,
    },
  }
}

function PropertyInput({ label, value, onChange, type = 'text', disabled = false }) {
  const normalizedValue = value == null ? '' : String(value)
  const [draftValue, setDraftValue] = useState(normalizedValue)

  useEffect(() => {
    setDraftValue(normalizedValue)
  }, [normalizedValue])

  function commitValue() {
    if (disabled || draftValue === normalizedValue) {
      return
    }

    onChange(draftValue)
  }

  return (
    <label className="field-group">
      <span className="field-label">{label}</span>
      <input
        className="field-input"
        type={type}
        value={draftValue}
        disabled={disabled}
        onChange={(event) => setDraftValue(event.target.value)}
        onBlur={commitValue}
        onKeyDown={(event) => {
          if (event.key === 'Enter') {
            event.currentTarget.blur()
          }

          if (event.key === 'Escape') {
            setDraftValue(normalizedValue)
            event.currentTarget.blur()
          }
        }}
      />
    </label>
  )
}

const CanvasItemCard = React.memo(function CanvasItemCard({ item, isSelected, onSelectItem, onDeleteItem }) {
  const isSquarePara = item.widgetType === 'PhaseSinglePara'
  const bindingCount = getItemBindingCount(item)
  const optionCount = isCompositeWidget(item) ? getDropdownItems(item).length : 0

  return (
    <div
      role="button"
      tabIndex={0}
      className={isSquarePara
        ? `canvas-square-item ${isSelected ? 'selected-canvas-node' : ''}`
        : `canvas-child ${isSelected ? 'selected-canvas-node' : ''}`}
      onClick={(event) => {
        event.stopPropagation()
        onSelectItem(item.id)
      }}
      onKeyDown={(event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault()
          event.stopPropagation()
          onSelectItem(item.id)
        }
      }}
    >
      <button
        type="button"
        className="item-delete-button"
        onClick={(event) => {
          event.stopPropagation()
          onDeleteItem(item.id)
        }}
        aria-label={`Delete ${item.id}`}
        title="Delete"
      >
        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><polyline points="3 6 5 6 21 6"></polyline><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"></path></svg>
      </button>

      <div className="canvas-child-head compact-item-head">
        <span className="canvas-chip compact-chip">{item.widgetType}</span>
        <strong>{item.label || item.id}</strong>
      </div>
      <div className="canvas-child-body compact-item-body">
        <span>{item.id}</span>
        <small>{isCompositeWidget(item) ? `Tag slots: ${bindingCount}, Options: ${optionCount}` : `Tag bind number: ${bindingCount}`}</small>
      </div>
    </div>
  )
}, (previousProps, nextProps) => previousProps.item === nextProps.item && previousProps.isSelected === nextProps.isSelected)

const CanvasPanelCard = React.memo(function CanvasPanelCard({
  panel,
  isSelected,
  isCollapsed,
  selectedItemId,
  onSelectPanel,
  onToggleCollapsed,
  onDeletePanel,
  onSelectItem,
  onDeleteItem,
  onAddChild,
}) {
  const collapseKey = `canvas-panel:${panel.id}`

  return (
    <div
      role="button"
      tabIndex={0}
      className={`canvas-child canvas-root-panel ${isSelected ? 'selected-canvas-node' : ''}`}
      onClick={() => onSelectPanel(panel.id)}
      onKeyDown={(event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault()
          onSelectPanel(panel.id)
        }
      }}
    >
      <div className="canvas-panel-toolbar">
        <div
          className="tree-toggle canvas-collapse-toggle"
          role="button"
          tabIndex={0}
          onClick={(event) => {
            event.stopPropagation()
            onToggleCollapsed(collapseKey)
          }}
          onKeyDown={(event) => {
            if (event.key === 'Enter' || event.key === ' ') {
              event.preventDefault()
              event.stopPropagation()
              onToggleCollapsed(collapseKey)
            }
          }}
        >
          {isCollapsed ? (
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><polyline points="9 18 15 12 9 6"></polyline></svg>
          ) : (
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><polyline points="6 9 12 15 18 9"></polyline></svg>
          )}
        </div>

        <div className="canvas-child-head">
          <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
            <span className="canvas-chip">{panel.panelType}</span>
            <span style={{ fontSize: '12px', color: '#607089' }}>{panel.items.length} items</span>
          </div>
          <strong>{panel.title}</strong>
        </div>

        <div className="canvas-inline-actions">
          <button
            type="button"
            className="icon-button"
            onClick={(event) => {
              event.stopPropagation()
              onDeletePanel(panel.id)
            }}
            aria-label={`Delete ${panel.id}`}
            title="Delete Panel"
            style={{ color: '#ef4444', backgroundColor: '#fee2e2' }}
          >
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><polyline points="3 6 5 6 21 6"></polyline><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"></path></svg>
          </button>
        </div>
      </div>

      <div className="canvas-child-body" style={{ display: 'none' }}>
        <span>{panel.id}</span>
        <small>{panel.items.length} items</small>
      </div>

      {isCollapsed ? null : (
        <div className="canvas-children">
          {panel.items.length > 0 ? panel.items.map((item) => (
            <CanvasItemCard
              key={item.id}
              item={item}
              isSelected={selectedItemId === item.id}
              onSelectItem={onSelectItem}
              onDeleteItem={onDeleteItem}
            />
          )) : null}

          <div className="canvas-add-card-wrap">
            <button
              type="button"
              className="canvas-add-card"
              onClick={(event) => {
                event.stopPropagation()
                onAddChild(panel.id)
              }}
              aria-label={`Add child to ${panel.id}`}
              title="Add Child Object"
            >
              + Add
            </button>
          </div>
        </div>
      )}
    </div>
  )
}, (previousProps, nextProps) => (
  previousProps.panel === nextProps.panel
  && previousProps.isSelected === nextProps.isSelected
  && previousProps.isCollapsed === nextProps.isCollapsed
  && previousProps.selectedItemId === nextProps.selectedItemId
))

const TreeRow = React.memo(function TreeRow({
  node,
  style,
  dragHandle,
  isSelected,
  onSelectNode,
}) {
  return (
    <div
      style={style}
      className={`tree-node tree-row ${isSelected ? 'selected-node' : ''}`}
      onClick={() => onSelectNode(node.id)}
      ref={dragHandle}
    >
      {node.data.type !== 'item' ? (
        <span
          className="tree-toggle"
          onClick={(event) => {
            event.stopPropagation()
            node.toggle()
          }}
        >
          {node.isOpen ? (
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><polyline points="6 9 12 15 18 9"></polyline></svg>
          ) : (
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><polyline points="9 18 15 12 9 6"></polyline></svg>
          )}
        </span>
      ) : (
        <span style={{ width: 20, display: 'inline-block' }} />
      )}

      <span style={{ marginRight: 6, display: 'flex', alignItems: 'center', color: '#607089' }}>
        {node.data.type === 'document' ? (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"></path></svg>
        ) : node.data.type === 'panel' ? (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><rect x="3" y="3" width="18" height="18" rx="2" ry="2"></rect><line x1="3" y1="9" x2="21" y2="9"></line><line x1="9" y1="21" x2="9" y2="9"></line></svg>
        ) : (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><rect x="3" y="3" width="18" height="18" rx="2" ry="2"></rect><circle cx="8.5" cy="8.5" r="1.5"></circle><polyline points="21 15 16 10 5 21"></polyline></svg>
        )}
      </span>

      <span className="tree-node-main">
        <strong>{node.data.name}</strong>
        <small>
          {node.data.type === 'item'
            ? node.data.widgetType
            : `${node.data.count} ${node.data.type === 'document' ? 'panels' : 'items'}`}
        </small>
      </span>
    </div>
  )
}, (previousProps, nextProps) => (
  previousProps.isSelected === nextProps.isSelected
  && previousProps.node.id === nextProps.node.id
  && previousProps.node.isOpen === nextProps.node.isOpen
  && previousProps.node.data === nextProps.node.data
  && previousProps.dragHandle === nextProps.dragHandle
  && previousProps.style?.top === nextProps.style?.top
  && previousProps.style?.left === nextProps.style?.left
  && previousProps.style?.height === nextProps.style?.height
  && previousProps.style?.width === nextProps.style?.width
  && previousProps.style?.transform === nextProps.style?.transform
))

function findPanelById(document, panelId) {
  return document.sections.find((panel) => panel.id === panelId) ?? null
}

function findItemById(document, itemId) {
  for (const panel of document.sections) {
    const item = panel.items.find((entry) => entry.id === itemId)
    if (item) {
      return { panel, item }
    }
  }

  return null
}

function getSelection(document, selectedKey) {
  if (selectedKey === 'document') {
    return { type: 'document', node: document }
  }

  if (selectedKey.startsWith('panel:')) {
    const panelId = selectedKey.slice(6)
    const panel = findPanelById(document, panelId) ?? document.sections[0]
    return { type: 'panel', node: panel }
  }

  if (selectedKey.startsWith('item:')) {
    const itemId = selectedKey.slice(5)
    const result = findItemById(document, itemId)
    if (result) {
      return { type: 'item', node: result.item, panel: result.panel }
    }
  }

  return { type: 'document', node: document }
}

function countAllObjects(document) {
  return document.sections.reduce((count, panel) => count + 1 + panel.items.length, 0)
}

function updatePanel(document, panelId, updater) {
  return {
    ...document,
    sections: document.sections.map((panel) => (panel.id === panelId ? updater(panel) : panel)),
  }
}

function updateItem(document, itemId, updater) {
  const panelIndex = document.sections.findIndex((panel) => panel.items.some((item) => item.id === itemId))
  if (panelIndex === -1) {
    return document
  }

  const currentPanel = document.sections[panelIndex]
  const itemIndex = currentPanel.items.findIndex((item) => item.id === itemId)
  if (itemIndex === -1) {
    return document
  }

  const currentItem = currentPanel.items[itemIndex]
  const nextItem = updater(currentItem)
  if (nextItem === currentItem) {
    return document
  }

  const nextItems = [...currentPanel.items]
  nextItems[itemIndex] = nextItem

  const nextSections = [...document.sections]
  nextSections[panelIndex] = {
    ...currentPanel,
    items: nextItems,
  }

  return {
    ...document,
    sections: nextSections,
  }
}

function getUniqueItemId(document, proposedId, currentId) {
  const existingIds = new Set()

  for (const panel of document.sections) {
    for (const item of panel.items) {
      if (item.id !== currentId) {
        existingIds.add(item.id)
      }
    }
  }

  if (!existingIds.has(proposedId)) {
    return proposedId
  }

  let suffix = 1
  while (existingIds.has(`${proposedId}_${suffix}`)) {
    suffix += 1
  }

  return `${proposedId}_${suffix}`
}

function validateDocumentLayout(document) {
  const issues = []

  if (!document || typeof document !== 'object' || Array.isArray(document)) {
    return ['JSON 顶层必须是对象。']
  }

  if (!Array.isArray(document.sections)) {
    issues.push('缺少 sections 数组。')
    return issues
  }

  if (document.sections.length === 0) {
    issues.push('sections 至少需要包含一个根 panel。')
  }

  const ids = new Set()

  document.sections.forEach((panel, panelIndex) => {
    if (!panel || typeof panel !== 'object' || Array.isArray(panel)) {
      issues.push(`sections[${panelIndex}] 必须是对象。`)
      return
    }

    if (!panel.id || typeof panel.id !== 'string') {
      issues.push(`sections[${panelIndex}] 缺少有效 id。`)
    } else if (ids.has(panel.id)) {
      issues.push(`存在重复 ID: ${panel.id}`)
    } else {
      ids.add(panel.id)
    }

    if (!panel.panelType || typeof panel.panelType !== 'string') {
      issues.push(`panel ${panel.id || `[${panelIndex}]`} 缺少 panelType。`)
    }

    if (!panel.title || typeof panel.title !== 'string') {
      issues.push(`panel ${panel.id || `[${panelIndex}]`} 缺少 title。`)
    }

    if (!panel.rowLayoutPath || typeof panel.rowLayoutPath !== 'string') {
      issues.push(`panel ${panel.id || `[${panelIndex}]`} 缺少 rowLayoutPath。`)
    }

    if (typeof panel.rowLayoutHorizontalGap !== 'number') {
      issues.push(`panel ${panel.id || `[${panelIndex}]`} 缺少 rowLayoutHorizontalGap。`)
    }

    if (typeof panel.sectionVerticalGap !== 'number') {
      issues.push(`panel ${panel.id || `[${panelIndex}]`} 缺少 sectionVerticalGap。`)
    }

    if (!Array.isArray(panel.items)) {
      issues.push(`panel ${panel.id || `[${panelIndex}]`} 缺少 items 数组。`)
      return
    }

    panel.items.forEach((item, itemIndex) => {
      if (!item || typeof item !== 'object' || Array.isArray(item)) {
        issues.push(`panel ${panel.id || `[${panelIndex}]`} 的 items[${itemIndex}] 必须是对象。`)
        return
      }

      if (!item.id || typeof item.id !== 'string') {
        issues.push(`panel ${panel.id || `[${panelIndex}]`} 的 items[${itemIndex}] 缺少有效 id。`)
      } else if (ids.has(item.id)) {
        issues.push(`存在重复 ID: ${item.id}`)
      } else {
        ids.add(item.id)
      }

      if (!item.widgetType || typeof item.widgetType !== 'string') {
        issues.push(`item ${item.id || `[${itemIndex}]`} 缺少 widgetType。`)
      }

      if (widgetTypeRequiresLabel(item.widgetType) && (!item.label || typeof item.label !== 'string')) {
        issues.push(`item ${item.id || `[${itemIndex}]`} 缺少 label。`)
      }

      if (isCompositeWidget(item)) {
        const schema = getCompositeWidgetSchema(item.widgetType)

        if (!item.slots || typeof item.slots !== 'object' || Array.isArray(item.slots)) {
          issues.push(`item ${item.id || `[${itemIndex}]`} 缺少 slots 对象。`)
          return
        }

        ;(schema.fixedSlotKeys ?? []).forEach((slotKey) => {
          const slotState = item.slots[slotKey]
          if (!slotState || typeof slotState !== 'object' || Array.isArray(slotState)) {
            issues.push(`item ${item.id || `[${itemIndex}]`} 缺少 slot ${slotKey}。`)
            return
          }

          if (!slotState.sourceTagPath || typeof slotState.sourceTagPath !== 'string') {
            issues.push(`item ${item.id || `[${itemIndex}]`} 缺少 slot ${slotKey}.sourceTagPath。`)
          }
        })

        const dropdownItems = getDropdownItems(item, schema.dropdownSlotKey)
        if (dropdownItems.length === 0) {
          issues.push(`item ${item.id || `[${itemIndex}]`} 缺少 ${schema.dropdownSlotKey}.items。`)
        }

        dropdownItems.forEach((entry, optionIndex) => {
          if (!entry.label || typeof entry.label !== 'string') {
            issues.push(`item ${item.id || `[${itemIndex}]`} 的 ${schema.dropdownSlotKey}.items[${optionIndex}] 缺少 label。`)
          }

          if (!entry.value || typeof entry.value !== 'string') {
            issues.push(`item ${item.id || `[${itemIndex}]`} 的 ${schema.dropdownSlotKey}.items[${optionIndex}] 缺少 value。`)
            return
          }

          const mappedBindings = getConditionOptionBindings(schema, entry)
          if (!Array.isArray(mappedBindings) || mappedBindings.length === 0) {
            issues.push(`item ${item.id || `[${itemIndex}]`} 的 ${schema.dropdownSlotKey}.items[${optionIndex}] 没有对应的 tag 绑定定义。`)
            return
          }

          mappedBindings.forEach(({ slotKey, sourceTagPath }) => {
            if (!sourceTagPath || typeof sourceTagPath !== 'string') {
              issues.push(`item ${item.id || `[${itemIndex}]`} 的 ${schema.dropdownSlotKey}.items[${optionIndex}] 缺少对应 slot ${slotKey}。`)
              return
            }
          })
        })

        return
      }

      const bindings = getItemBindings(item)
      if (bindings.length === 0) {
        issues.push(`item ${item.id || `[${itemIndex}]`} 缺少 bind 或 binds。`)
        return
      }

      bindings.forEach((binding, bindingIndex) => {
        const bindingPath = Array.isArray(item.binds) ? `binds[${bindingIndex}]` : 'bind'

        if (!binding.uiProperty || typeof binding.uiProperty !== 'string') {
          issues.push(`item ${item.id || `[${itemIndex}]`} 缺少 ${bindingPath}.uiProperty。`)
        }

        if (!binding.sourceTagPath || typeof binding.sourceTagPath !== 'string') {
          issues.push(`item ${item.id || `[${itemIndex}]`} 缺少 ${bindingPath}.sourceTagPath。`)
          }
        })
    })
  })

  return issues
}

function moveArrayItem(items, fromIndex, toIndex) {
  const nextItems = [...items]
  const [movingItem] = nextItems.splice(fromIndex, 1)
  nextItems.splice(toIndex, 0, movingItem)
  return nextItems
}

const fixedPhaseConfigFileName = 'phase_ui_layout.sample.json'

const phaseConfigOptions = [
  fixedPhaseConfigFileName,
]

const phaseConfigApiBaseUrl = 'http://127.0.0.1:8099/api/phase-config'

async function saveJsonToLocalFile(fileName, serializedLayout) {
  if (typeof window !== 'undefined' && 'showSaveFilePicker' in window) {
    const fileHandle = await window.showSaveFilePicker({
      suggestedName: fileName,
      types: [
        {
          description: 'JSON Files',
          accept: {
            'application/json': ['.json'],
          },
        },
      ],
    })

    const writable = await fileHandle.createWritable()
    await writable.write(serializedLayout)
    await writable.close()
    return 'file-system-access'
  }

  const blob = new Blob([serializedLayout], { type: 'application/json;charset=utf-8' })
  const downloadUrl = URL.createObjectURL(blob)
  const anchor = document.createElement('a')

  anchor.href = downloadUrl
  anchor.download = fileName
  anchor.style.display = 'none'
  document.body.append(anchor)
  anchor.click()
  anchor.remove()
  URL.revokeObjectURL(downloadUrl)

  return 'download'
}

export default function App() {
  const [documentLayout, setDocumentLayout] = useState(createDefaultDocument)
  const [selectedKey, setSelectedKey] = useState('panel:ParaPanel1')
  const [notice, setNotice] = useState('Switched to document + sections structure. You can add multiple root panels.')
  const [selectedConfigFile, setSelectedConfigFile] = useState(phaseConfigOptions[0])
  const [isRequestPending, setIsRequestPending] = useState(false)
  const [collapsedNodes, setCollapsedNodes] = useState({
    document: false,
  })
  const documentLayoutRef = useRef(documentLayout)
  const treeContainerRef = useRef(null)
  const [treeHeight, setTreeHeight] = useState(0)

  documentLayoutRef.current = documentLayout

  // 将 documentLayout 转换为 react-arborist 需要的格式
  const treeData = useMemo(() => {
    return [
      {
        id: 'document',
        name: 'UI Root',
        type: 'document',
        count: documentLayout.sections.length,
        children: documentLayout.sections.map((panel, panelIndex) => ({
          id: `panel:${panel.id}`,
          name: panel.title || panel.id,
          type: 'panel',
          panelIndex,
          count: panel.items.length,
          children: panel.items.map((item, itemIndex) => ({
            id: `item:${item.id}`,
            name: item.label || item.id,
            widgetType: item.widgetType,
            type: 'item',
            itemIndex,
          }))
        }))
      }
    ]
  }, [documentLayout])

  const selection = useMemo(() => getSelection(documentLayout, selectedKey), [documentLayout, selectedKey])

  useEffect(() => {
    const element = treeContainerRef.current
    if (!element) {
      return undefined
    }

    function updateTreeHeight() {
      const nextHeight = Math.max(0, Math.floor(element.getBoundingClientRect().height))
      setTreeHeight((currentHeight) => (currentHeight === nextHeight ? currentHeight : nextHeight))
    }

    updateTreeHeight()

    const resizeObserver = new ResizeObserver(() => {
      updateTreeHeight()
    })

    resizeObserver.observe(element)

    return () => {
      resizeObserver.disconnect()
    }
  }, [])

  useEffect(() => {
    if (findPanelById(documentLayout, 'ParaPanel1') && selectedKey === 'panel:ParaPanel1') {
      return
    }

    if (selection.type === 'document' && documentLayout.sections[0]) {
      setSelectedKey(`panel:${documentLayout.sections[0].id}`)
    }
  }, [documentLayout, selectedKey, selection.type])

  function isCollapsed(nodeKey) {
    return collapsedNodes[nodeKey] === true
  }

  function toggleCollapsed(nodeKey) {
    setCollapsedNodes((current) => ({
      ...current,
      [nodeKey]: !current[nodeKey],
    }))
  }

  function resetLayout() {
    const nextDocument = createDefaultDocument()
    setDocumentLayout(nextDocument)
    setSelectedKey(`panel:${nextDocument.sections[0].id}`)
    setNotice('Reset to default document and first root panel.')
  }

  function addPanel() {
    const nextPanel = createPanel(documentLayout.sections.length)
    setDocumentLayout({
      ...documentLayout,
      sections: [...documentLayout.sections, nextPanel],
    })
    setSelectedKey(`panel:${nextPanel.id}`)
    setNotice(`Added root panel ${nextPanel.id}.`)
  }

  function addChildToPanel(panelId) {
    const panel = findPanelById(documentLayout, panelId)
    if (!panel) {
      setNotice('Target panel not found.')
      return
    }

    const nextChild = createChildItem(documentLayout)
    setDocumentLayout(updatePanel(documentLayout, panelId, (currentPanel) => ({
      ...currentPanel,
      items: [...currentPanel.items, nextChild],
    })))
    setSelectedKey(`item:${nextChild.id}`)
    setNotice(`Added child object ${nextChild.id} under ${panelId}.`)
  }

  function deletePanelById(panelId) {
    const panel = findPanelById(documentLayout, panelId)
    if (!panel) {
      setNotice('Target panel not found.')
      return
    }

    if (documentLayout.sections.length === 1) {
      setNotice('At least one root panel must be kept.')
      return
    }

    const panelIndex = documentLayout.sections.findIndex((entry) => entry.id === panelId)
    const nextSections = documentLayout.sections.filter((entry) => entry.id !== panelId)
    const fallbackPanel = nextSections[panelIndex] ?? nextSections[panelIndex - 1] ?? nextSections[0]

    setDocumentLayout({
      ...documentLayout,
      sections: nextSections,
    })
    setSelectedKey(`panel:${fallbackPanel.id}`)
    setNotice(`Deleted root panel ${panelId}.`)
  }

  function deleteItemById(itemId) {
    const result = findItemById(documentLayout, itemId)
    if (!result) {
      setNotice('Target child object not found.')
      return
    }

    const parentPanel = result.panel
    const itemIndex = parentPanel.items.findIndex((item) => item.id === itemId)
    const nextItems = parentPanel.items.filter((item) => item.id !== itemId)
    const fallbackItem = nextItems[itemIndex] ?? nextItems[itemIndex - 1] ?? null

    setDocumentLayout(updatePanel(documentLayout, parentPanel.id, (panel) => ({
      ...panel,
      items: nextItems,
    })))
    setSelectedKey(fallbackItem ? `item:${fallbackItem.id}` : `panel:${parentPanel.id}`)
    setNotice(`Deleted child object ${itemId}.`)
  }

  function moveSelected(direction) {
    if (selection.type === 'document') {
      setNotice('Document node cannot be reordered.')
      return
    }

    if (selection.type === 'panel') {
      const index = documentLayout.sections.findIndex((panel) => panel.id === selection.node.id)
      const targetIndex = index + direction

      if (index === -1 || targetIndex < 0 || targetIndex >= documentLayout.sections.length) {
        setNotice('Current panel is already at the boundary, cannot move further.')
        return
      }

      const nextSections = moveArrayItem(documentLayout.sections, index, targetIndex)

      setDocumentLayout({
        ...documentLayout,
        sections: nextSections,
      })
      setNotice(`Moved root panel ${selection.node.id}.`)
      return
    }

    const parentPanel = selection.panel
    const index = parentPanel.items.findIndex((item) => item.id === selection.node.id)
    const targetIndex = index + direction

    if (index === -1 || targetIndex < 0 || targetIndex >= parentPanel.items.length) {
      setNotice('Current child object is already at the boundary, cannot move further.')
      return
    }

    const nextItems = moveArrayItem(parentPanel.items, index, targetIndex)

    setDocumentLayout(updatePanel(documentLayout, parentPanel.id, (panel) => ({
      ...panel,
      items: nextItems,
    })))
    setNotice(`Moved child object ${selection.node.id}.`)
  }

  function handleTreeMove({ dragIds, parentId, index }) {
    const dragId = dragIds[0]
    
    // 不允许拖拽根节点
    if (dragId === 'document') return

    // 获取拖拽的节点类型和真实ID
    const isPanel = dragId.startsWith('panel:')
    const isItem = dragId.startsWith('item:')
    const realDragId = dragId.replace(/^(panel:|item:)/, '')

    // 拖拽 Panel
    if (isPanel) {
      // Panel 只能放在 document 下
      if (parentId !== 'document') {
        setNotice('Panel can only be a root node, cannot be placed inside other nodes.')
        return
      }

      const currentIndex = documentLayout.sections.findIndex(p => p.id === realDragId)
      if (currentIndex === -1) return

      const nextSections = moveArrayItem(
        documentLayout.sections,
        currentIndex,
        currentIndex < index ? index - 1 : index,
      )

      // 如果是在同一个父节点下往下拖，由于前面删除了一个元素，目标 index 需要减 1
      setDocumentLayout({
        ...documentLayout,
        sections: nextSections,
      })
      setNotice(`Moved root panel ${realDragId}.`)
      return
    }

    // 拖拽 Item
    if (isItem) {
      // Item 只能放在 Panel 下
      if (!parentId || !parentId.startsWith('panel:')) {
        setNotice('Child object can only be placed inside a Panel.')
        return
      }

      const targetPanelId = parentId.replace('panel:', '')
      const sourceResult = findItemById(documentLayout, realDragId)
      
      if (!sourceResult) return
      
      const sourcePanel = sourceResult.panel
      const movingItem = sourceResult.item

      // 同一个 Panel 内移动
      if (sourcePanel.id === targetPanelId) {
        const currentIndex = sourcePanel.items.findIndex(i => i.id === realDragId)
        const nextItems = moveArrayItem(
          sourcePanel.items,
          currentIndex,
          currentIndex < index ? index - 1 : index,
        )

        setDocumentLayout(updatePanel(documentLayout, sourcePanel.id, panel => ({
          ...panel,
          items: nextItems
        })))
      } 
      // 跨 Panel 移动
      else {
        setDocumentLayout(prev => {
          const nextSections = prev.sections.map(panel => {
            if (panel.id === sourcePanel.id) {
              return {
                ...panel,
                items: panel.items.filter(i => i.id !== realDragId)
              }
            }
            if (panel.id === targetPanelId) {
              const nextItems = [...panel.items]
              nextItems.splice(index, 0, movingItem)
              return {
                ...panel,
                items: nextItems
              }
            }
            return panel
          })
          return { ...prev, sections: nextSections }
        })
      }
      setNotice(`Moved child object ${realDragId}.`)
    }
  }

  function updatePanelField(field, rawValue) {
    const value = field === 'rowLayoutHorizontalGap' || field === 'sectionVerticalGap'
      ? Number(rawValue)
      : rawValue

    setDocumentLayout(updatePanel(documentLayout, selection.node.id, (panel) => ({
      ...panel,
      [field]: value,
    })))
  }

  function updateItemField(field, value) {
    if (field === 'id') {
      const nextId = getUniqueItemId(documentLayout, value, selection.node.id)
      setDocumentLayout(updateItem(documentLayout, selection.node.id, (item) => ({
        ...item,
        id: nextId,
      })))
      setSelectedKey(`item:${nextId}`)
      if (nextId !== value) {
        setNotice(`Child object ID ${value} already exists, automatically adjusted to ${nextId}.`)
      }
      return
    }

    setDocumentLayout(updateItem(documentLayout, selection.node.id, (item) => ({
      ...item,
      [field]: value,
    })))
  }

  function updateItemBindField(bindingIndex, field, value) {
    setDocumentLayout(updateItem(documentLayout, selection.node.id, (item) => ({
      ...updateItemBinding(item, bindingIndex, field, value),
    })))
  }

  function updateCompositeSlotField(slotKey, value) {
    setDocumentLayout(updateItem(documentLayout, selection.node.id, (item) => updateCompositeSlotBinding(item, slotKey, value)))
  }

  function updateConditionOptionBindingField(optionIndex, slotKey, value) {
    setDocumentLayout(updateItem(documentLayout, selection.node.id, (item) => updateConditionOptionBinding(item, 'conditionSelector', optionIndex, slotKey, value)))
  }

  function addConditionOptionBindingField(optionIndex, slotKey) {
    setDocumentLayout(updateItem(documentLayout, selection.node.id, (item) => addConditionOptionBinding(item, 'conditionSelector', optionIndex, slotKey)))
    setNotice(`Added tag binding ${slotKey} for option ${optionIndex + 1} in ${selection.node.id}.`)
  }

  function removeConditionOptionBindingField(optionIndex, slotKey) {
    setDocumentLayout(updateItem(documentLayout, selection.node.id, (item) => removeConditionOptionBinding(item, 'conditionSelector', optionIndex, slotKey)))
    setNotice(`Removed tag binding ${slotKey} from option ${optionIndex + 1} in ${selection.node.id}.`)
  }

  function updateConditionSelectorItemField(optionIndex, field, value) {
    setDocumentLayout(updateItem(documentLayout, selection.node.id, (item) => updateDropdownItem(item, 'conditionSelector', optionIndex, field, value)))
  }

  function addConditionSelectorItem() {
    setDocumentLayout(updateItem(documentLayout, selection.node.id, (item) => addDropdownItem(item, 'conditionSelector')))
    setNotice(`Added dropdown option for ${selection.node.id}.`)
  }

  function removeConditionSelectorItem(optionIndex) {
    setDocumentLayout(updateItem(documentLayout, selection.node.id, (item) => removeDropdownItem(item, 'conditionSelector', optionIndex)))
    setNotice(`Removed dropdown option ${optionIndex + 1} from ${selection.node.id}.`)
  }

  function moveConditionSelectorItem(optionIndex, direction) {
    const currentItems = getDropdownItems(selection.node, 'conditionSelector')
    const targetIndex = optionIndex + direction

    if (targetIndex < 0 || targetIndex >= currentItems.length) {
      return
    }

    setDocumentLayout(updateItem(documentLayout, selection.node.id, (item) => moveDropdownItem(item, 'conditionSelector', optionIndex, targetIndex)))
  }

  function addItemBinding() {
    setDocumentLayout(updateItem(documentLayout, selection.node.id, (item) => {
      const currentBindings = getItemBindings(item)

      if (Array.isArray(item.binds)) {
        return {
          ...item,
          binds: [...currentBindings, createDefaultBinding()],
        }
      }

      if (currentBindings.length === 0) {
        return {
          ...item,
          bind: createDefaultBinding(),
        }
      }

      return {
        ...item,
        bind: undefined,
        binds: [...currentBindings, createDefaultBinding()],
      }
    }))

    setNotice(`Added binding for ${selection.node.id}.`)
  }

  function removeItemBinding(bindingIndex) {
    const currentBindings = getItemBindings(selection.node)
    if (currentBindings.length <= 1) {
      setNotice('At least one binding must be kept.')
      return
    }

    setDocumentLayout(updateItem(documentLayout, selection.node.id, (item) => {
      const nextBindings = getItemBindings(item).filter((_, index) => index !== bindingIndex)

      if (nextBindings.length === 1) {
        return {
          ...item,
          binds: undefined,
          bind: nextBindings[0],
        }
      }

      return {
        ...item,
        bind: undefined,
        binds: nextBindings,
      }
    }))

    setNotice(`Removed binding ${bindingIndex + 1} from ${selection.node.id}.`)
  }

  const selectedPanelId = selection.type === 'panel'
    ? selection.node.id
    : selection.type === 'item'
      ? selection.panel.id
      : null

  const selectedItemId = selection.type === 'item' ? selection.node.id : null
  const selectionItemBindings = selection.type === 'item' ? getItemBindings(selection.node) : []
  const selectionDropdownItems = selection.type === 'item' && isCompositeWidget(selection.node)
    ? getDropdownItems(selection.node, 'conditionSelector')
    : []
  const selectionCompositeFixedBindings = selection.type === 'item' && isCompositeWidget(selection.node)
    ? getCompositeFixedBindings(selection.node)
    : []
  const selectionCompositeOptionGroups = selection.type === 'item' && isCompositeWidget(selection.node)
    ? getCompositeOptionBindingGroups(selection.node)
    : []

  function selectPanelById(panelId) {
    setSelectedKey(`panel:${panelId}`)
  }

  function selectItemById(itemId) {
    setSelectedKey(`item:${itemId}`)
  }

  function handleConfigFileChange(event) {
    const nextFile = event.target.value
    setSelectedConfigFile(nextFile)
    setNotice(`Switched current config file to ${nextFile}.`)
  }

  async function postPhaseConfig(endpoint, payload) {
    const response = await fetch(`${phaseConfigApiBaseUrl}/${endpoint}`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(payload),
    })

    const result = await response.json()
    if (!response.ok || !result.ok) {
      throw new Error(result.error || `Request failed with status ${response.status}.`)
    }

    return result
  }

  async function handleGetJson() {
    setIsRequestPending(true)

    try {
      const parsedLayout = structuredClone(samplePhaseLayout)
      const issues = validateDocumentLayout(parsedLayout)

      if (issues.length > 0) {
        throw new Error(issues[0])
      }

      setDocumentLayout(parsedLayout)
      setSelectedKey(parsedLayout.sections[0] ? `panel:${parsedLayout.sections[0].id}` : 'document')
      setSelectedConfigFile(fixedPhaseConfigFileName)
      setNotice(`Loaded ${fixedPhaseConfigFileName}.`)
    } catch (error) {
      setNotice(`Get Json failed: ${error.message}`)
    } finally {
      setIsRequestPending(false)
    }
  }

  async function handleSaveLayout() {
    flushSync(() => {
      if (document.activeElement instanceof HTMLElement) {
        document.activeElement.blur()
      }
    })

    const latestDocumentLayout = documentLayoutRef.current
    const issues = validateDocumentLayout(latestDocumentLayout)
    if (issues.length > 0) {
      setNotice(`Save failed: ${issues[0]}`)
      return
    }

    setIsRequestPending(true)

    try {
      const serializedLayout = JSON.stringify(latestDocumentLayout, null, 2)
      const saveMode = await saveJsonToLocalFile(selectedConfigFile || fixedPhaseConfigFileName, serializedLayout)

      if (saveMode === 'file-system-access') {
        setNotice(`Saved ${selectedConfigFile || fixedPhaseConfigFileName} to a local file.`)
      } else {
        setNotice(`Downloaded ${selectedConfigFile || fixedPhaseConfigFileName}.`)
      }
    } catch (error) {
      if (error?.name === 'AbortError') {
        setNotice('Save cancelled.')
      } else {
        setNotice(`Save failed: ${error.message}`)
      }
    } finally {
      setIsRequestPending(false)
    }
  }

  return (
    <div className="app-shell">
      <header className="file-toolbar">
        <div className="file-toolbar-left">
          <div className="file-toolbar-select-wrap">
            <select
              className="file-toolbar-select"
              value={selectedConfigFile}
              onChange={handleConfigFileChange}
              disabled={isRequestPending}
              aria-label="Select phase config file"
            >
              {phaseConfigOptions.map((fileName) => (
                <option key={fileName} value={fileName}>{fileName}</option>
              ))}
            </select>
            <span className="file-toolbar-arrow" aria-hidden="true">
              <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><polyline points="6 9 12 15 18 9"></polyline></svg>
            </span>
          </div>

          <button type="button" className="file-toolbar-secondary" onClick={handleGetJson} disabled={isRequestPending}>Get Json</button>
        </div>

        <button type="button" className="file-toolbar-save" onClick={handleSaveLayout} disabled={isRequestPending}>
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z"></path><polyline points="17 21 17 13 7 13 7 21"></polyline><polyline points="7 3 7 8 15 8"></polyline></svg>
          <span>Save</span>
        </button>
      </header>

      <main className="workspace theme-recipe-editor">
        <section className="panel panel-tree">
          <div className="panel-header">
            <div>
              <h2>UI Tree</h2>
              <span>{countAllObjects(documentLayout)} objects</span>
            </div>
          </div>

          <div ref={treeContainerRef} className="tree-container" style={{ flexGrow: 1, height: '100%', minHeight: 0 }}>
            {treeHeight > 0 ? (
              <Tree
                data={treeData}
                width="100%"
                height={treeHeight}
                rowHeight={44}
                indent={16}
                onMove={handleTreeMove}
                openByDefault={true}
                selection={selectedKey}
              >
              {({ node, style, dragHandle }) => {
                return (
                  <TreeRow
                    node={node}
                    style={style}
                    dragHandle={dragHandle}
                    isSelected={selectedKey === node.id}
                    onSelectNode={setSelectedKey}
                  />
                )
              }}
              </Tree>
            ) : null}
          </div>
        </section>

        <section className="panel panel-canvas panel-workspace">
          <div className="canvas-surface">
            <div className="canvas-container">
              {documentLayout.sections.length === 0 ? (
                <div className="empty-state" style={{ textAlign: 'center', padding: '40px 20px', border: '2px dashed #cbd5e1', background: 'transparent' }}>
                  <p style={{ margin: '0 0 16px', fontSize: '14px', color: '#64748b' }}>No panels in the current document</p>
                  <button type="button" className="primary-button" onClick={addPanel}>+ Add Root Panel</button>
                </div>
              ) : (
                <div className="canvas-children canvas-panels-list">
                  {documentLayout.sections.map((panel) => (
                    <CanvasPanelCard
                      key={panel.id}
                      panel={panel}
                      isSelected={selectedPanelId === panel.id}
                      isCollapsed={isCollapsed(`canvas-panel:${panel.id}`)}
                      selectedItemId={selectedPanelId === panel.id ? selectedItemId : null}
                      onSelectPanel={selectPanelById}
                      onToggleCollapsed={toggleCollapsed}
                      onDeletePanel={deletePanelById}
                      onSelectItem={selectItemById}
                      onDeleteItem={deleteItemById}
                      onAddChild={addChildToPanel}
                    />
                  ))}

                  <button
                    type="button"
                    className="canvas-add-panel"
                    onClick={addPanel}
                  >
                    + Add Root Panel
                  </button>
                </div>
              )}
            </div>
          </div>
        </section>

        <section className="panel panel-properties panel-insertions">
          <div className="properties-scroll">
            <div className="properties-card">
              <div className="properties-title">
                <strong>
                  {selection.type === 'document'
                    ? 'Layout Document'
                    : selection.node.id}
                </strong>
                <span>
                  {selection.type === 'document'
                    ? 'document'
                    : selection.type === 'panel'
                      ? selection.node.panelType
                      : selection.node.widgetType}
                </span>
              </div>

              {selection.type === 'document' ? (
                <div className="tree-placeholder">Root node properties are hidden. Please select a Panel or Child Object to edit.</div>
              ) : null}

              {selection.type === 'panel' ? (
                <>
                  <PropertyInput label="id" value={selection.node.id} onChange={(value) => updatePanelField('id', value)} disabled />
                  <PropertyInput label="title" value={selection.node.title} onChange={(value) => updatePanelField('title', value)} />
                  <PropertyInput label="rowLayoutPath" value={selection.node.rowLayoutPath} onChange={(value) => updatePanelField('rowLayoutPath', value)} />
                  <PropertyInput
                    label="rowLayoutHorizontalGap"
                    value={selection.node.rowLayoutHorizontalGap}
                    type="number"
                    onChange={(value) => updatePanelField('rowLayoutHorizontalGap', value)}
                  />
                  <PropertyInput
                    label="sectionVerticalGap"
                    value={selection.node.sectionVerticalGap}
                    type="number"
                    onChange={(value) => updatePanelField('sectionVerticalGap', value)}
                  />
                </>
              ) : null}

              {selection.type === 'item' ? (
                <>
                  <PropertyInput label="id" value={selection.node.id} onChange={(value) => updateItemField('id', value)} disabled />
                  <PropertyInput label="label" value={selection.node.label} onChange={(value) => updateItemField('label', value)} />
                  <PropertyInput label="widgetType" value={selection.node.widgetType} onChange={(value) => updateItemField('widgetType', value)} />
                  {isCompositeWidget(selection.node) ? (
                    <>
                      <div style={{ marginTop: '14px', paddingTop: '14px', borderTop: '1px solid #e3ebf5' }}>
                        <strong style={{ display: 'block', marginBottom: '10px', fontSize: '13px', color: '#1f2937' }}>Fixed Tag Bindings</strong>
                        {selectionCompositeFixedBindings.map((binding, bindingIndex) => (
                          <div key={binding.slotKey} style={{ marginTop: bindingIndex === 0 ? '0' : '14px', paddingTop: bindingIndex === 0 ? '0' : '14px', borderTop: bindingIndex === 0 ? 'none' : '1px solid #e3ebf5' }}>
                            <PropertyInput
                              label={binding.label}
                              value={binding.sourceTagPath}
                              onChange={(value) => updateCompositeSlotField(binding.slotKey, value)}
                            />
                            <div style={{ fontSize: '11px', lineHeight: 1.5, color: '#64748b' }}>
                              <div>{`UI Property: ${binding.uiProperty}`}</div>
                              <div>{`UI Path: ${binding.uiPath}`}</div>
                            </div>
                          </div>
                        ))}
                      </div>

                      <div style={{ marginTop: '18px', paddingTop: '14px', borderTop: '1px solid #e3ebf5' }}>
                        <strong style={{ display: 'block', marginBottom: '10px', fontSize: '13px', color: '#1f2937' }}>Condition Selector Items</strong>
                        {selectionCompositeOptionGroups.map(({ option, optionIndex, bindings, slotKeys }) => {
                          const availableSlotKeys = getAvailableConditionOptionSlotKeys(selection.node, option)

                          return (
                          <div key={`condition-option-${optionIndex}`} style={{ marginTop: optionIndex === 0 ? '0' : '14px', paddingTop: optionIndex === 0 ? '0' : '14px', borderTop: optionIndex === 0 ? 'none' : '1px solid #e3ebf5' }}>
                            <PropertyInput
                              label={`Option ${optionIndex + 1} Label`}
                              value={option.label}
                              onChange={(value) => updateConditionSelectorItemField(optionIndex, 'label', value)}
                            />
                            <PropertyInput
                              label={`Option ${optionIndex + 1} Value`}
                              value={option.value}
                              onChange={(value) => updateConditionSelectorItemField(optionIndex, 'value', value)}
                            />
                            <div style={{ marginTop: '6px', fontSize: '11px', lineHeight: 1.5, color: '#64748b' }}>
                              {`slots: ${slotKeys.join(', ') || 'none'}`}
                            </div>
                            {availableSlotKeys.length > 0 ? (
                              <div style={{ marginTop: '8px' }}>
                                <div style={{ marginBottom: '6px', fontSize: '11px', lineHeight: 1.5, color: '#64748b' }}>Add Tag Binding</div>
                                <div className="action-list" style={{ gap: '6px', alignItems: 'center' }}>
                                  {availableSlotKeys.map((slotKey) => {
                                    const slotDefinition = getCompositeWidgetSchema(selection.node.widgetType)?.slots?.[slotKey]

                                    return (
                                      <button
                                        key={`${optionIndex}-add-${slotKey}`}
                                        type="button"
                                        className="ghost-button"
                                        style={{ fontSize: '12px' }}
                                        onClick={() => addConditionOptionBindingField(optionIndex, slotKey)}
                                      >
                                        {`Add ${slotDefinition?.label || slotKey}`}
                                      </button>
                                    )
                                  })}
                                </div>
                              </div>
                            ) : null}
                            {bindings.length > 0 ? (
                              <div style={{ marginTop: '10px', padding: '10px 12px', borderRadius: '10px', background: '#f8fafc', border: '1px solid #e3ebf5' }}>
                                <strong style={{ display: 'block', marginBottom: '8px', fontSize: '12px', color: '#1f2937' }}>Mapped Tag Bindings</strong>
                                {bindings.map((binding, bindingIndex) => (
                                  <div key={`${optionIndex}-${binding.slotKey}`} style={{ marginTop: bindingIndex === 0 ? '0' : '12px' }}>
                                    <PropertyInput
                                      label={binding.label}
                                      value={binding.sourceTagPath}
                                      onChange={(value) => updateConditionOptionBindingField(optionIndex, binding.slotKey, value)}
                                    />
                                    <div style={{ fontSize: '11px', lineHeight: 1.5, color: '#64748b' }}>
                                      <div>{`UI Property: ${binding.uiProperty}`}</div>
                                      <div>{`UI Path: ${binding.uiPath}`}</div>
                                    </div>
                                    <div className="action-list" style={{ marginTop: '6px', flexWrap: 'nowrap', alignItems: 'center' }}>
                                      <button type="button" className="ghost-button" style={{ fontSize: '12px' }} onClick={() => removeConditionOptionBindingField(optionIndex, binding.slotKey)}>Delete Binding</button>
                                    </div>
                                  </div>
                                ))}
                              </div>
                            ) : (
                              <div style={{ marginTop: '10px', fontSize: '12px', color: '#b45309' }}>
                                {slotKeys.length === 0 ? 'Current value has no mapped tag bindings.' : ''}
                              </div>
                            )}
                            <div className="action-list" style={{ marginTop: '8px', flexWrap: 'nowrap', alignItems: 'center' }}>
                              <button type="button" className="ghost-button" style={{ fontSize: '12px' }} onClick={() => moveConditionSelectorItem(optionIndex, -1)} disabled={optionIndex === 0}>Move Up</button>
                              <button type="button" className="ghost-button" style={{ fontSize: '12px' }} onClick={() => moveConditionSelectorItem(optionIndex, 1)} disabled={optionIndex === selectionCompositeOptionGroups.length - 1}>Move Down</button>
                              <button type="button" className="ghost-button" style={{ fontSize: '12px' }} onClick={() => removeConditionSelectorItem(optionIndex)}>Delete Item</button>
                            </div>
                          </div>
                        )})}
                        <div className="action-list" style={{ marginTop: '10px', flexWrap: 'nowrap', alignItems: 'center' }}>
                          <button type="button" className="primary-button" style={{ fontSize: '12px' }} onClick={addConditionSelectorItem}>Add Item</button>
                        </div>
                      </div>
                    </>
                  ) : (
                    selectionItemBindings.map((binding, bindingIndex) => {
                      const bindingLabel = Array.isArray(selection.node.binds) ? `binds[${bindingIndex}]` : 'bind'
                      const isLastBinding = bindingIndex === selectionItemBindings.length - 1

                      return (
                        <div key={bindingLabel} style={{ marginTop: bindingIndex === 0 ? '0' : '14px', paddingTop: bindingIndex === 0 ? '0' : '14px', borderTop: bindingIndex === 0 ? 'none' : '1px solid #e3ebf5' }}>
                          <PropertyInput
                            label={`${bindingLabel}.uiProperty`}
                            value={binding.uiProperty}
                            onChange={(value) => updateItemBindField(bindingIndex, 'uiProperty', value)}
                            disabled
                          />
                          <PropertyInput
                            label={`${bindingLabel}.sourceTagPath`}
                            value={binding.sourceTagPath}
                            onChange={(value) => updateItemBindField(bindingIndex, 'sourceTagPath', value)}
                          />
                          <div className="action-list" style={{ marginTop: '8px', flexWrap: 'nowrap', alignItems: 'center' }}>
                            <button type="button" className="ghost-button" style={{ fontSize: '12px' }} onClick={() => removeItemBinding(bindingIndex)}>Delete Binding</button>
                            {isLastBinding ? <button type="button" className="primary-button" style={{ fontSize: '12px' }} onClick={addItemBinding}>Add Binding</button> : null}
                          </div>
                        </div>
                      )
                    })
                  )}
                </>
              ) : null}

              {selection.type !== 'document' && (
                <div className="action-list" style={{ marginTop: '24px', paddingTop: '16px', borderTop: '1px solid #e3ebf5' }}>
                  <button type="button" className="ghost-button" onClick={() => moveSelected(-1)}>Move Up</button>
                  <button type="button" className="ghost-button" onClick={() => moveSelected(1)}>Move Down</button>
                </div>
              )}
            </div>

            <div className="notice-box" style={{ 
              display: 'flex', 
              alignItems: 'center', 
              gap: '8px',
              boxShadow: '0 2px 8px rgba(29, 78, 137, 0.08)'
            }}>
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><circle cx="12" cy="12" r="10"></circle><line x1="12" y1="16" x2="12" y2="12"></line><line x1="12" y1="8" x2="12.01" y2="8"></line></svg>
              {notice}
            </div>
          </div>
        </section>
      </main>
    </div>
  )
}
