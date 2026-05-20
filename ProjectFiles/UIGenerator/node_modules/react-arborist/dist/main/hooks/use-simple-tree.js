"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.useSimpleTree = useSimpleTree;
const react_1 = require("react");
const simple_tree_1 = require("../data/simple-tree");
let nextId = 0;
function useSimpleTree(initialData) {
    const [data, setData] = (0, react_1.useState)(initialData);
    const tree = (0, react_1.useMemo)(() => new simple_tree_1.SimpleTree(data), [data]);
    const onMove = (args) => {
        for (const id of args.dragIds) {
            tree.move({ id, parentId: args.parentId, index: args.index });
        }
        setData(tree.data);
    };
    const onRename = ({ name, id }) => {
        tree.update({ id, changes: { name } });
        setData(tree.data);
    };
    const onCreate = ({ parentId, index, type }) => {
        const data = { id: `simple-tree-id-${nextId++}`, name: "" };
        if (type === "internal")
            data.children = [];
        tree.create({ parentId, index, data });
        setData(tree.data);
        return data;
    };
    const onDelete = (args) => {
        args.ids.forEach((id) => tree.drop({ id }));
        setData(tree.data);
    };
    const controller = { onMove, onRename, onCreate, onDelete };
    return [data, controller];
}
