"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.useDragHook = useDragHook;
const react_1 = require("react");
const react_dnd_1 = require("react-dnd");
const react_dnd_html5_backend_1 = require("react-dnd-html5-backend");
const context_1 = require("../context");
const dnd_slice_1 = require("../state/dnd-slice");
function useDragHook(node) {
    const tree = (0, context_1.useTreeApi)();
    const ids = tree.selectedIds;
    const [_, ref, preview] = (0, react_dnd_1.useDrag)(() => ({
        canDrag: () => node.isDraggable,
        type: "NODE",
        item: () => {
            // This is fired once at the begging of a drag operation
            const dragIds = tree.isSelected(node.id) ? Array.from(ids) : [node.id];
            tree.dispatch(dnd_slice_1.actions.dragStart(node.id, dragIds));
            return { id: node.id, dragIds };
        },
        end: () => {
            tree.hideCursor();
            tree.dispatch(dnd_slice_1.actions.dragEnd());
        },
    }), [ids, node]);
    (0, react_1.useEffect)(() => {
        preview((0, react_dnd_html5_backend_1.getEmptyImage)());
    }, [preview]);
    return ref;
}
