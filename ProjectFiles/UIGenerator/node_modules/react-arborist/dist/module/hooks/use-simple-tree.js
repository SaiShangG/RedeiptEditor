import { useMemo, useState } from "react";
import { SimpleTree } from "../data/simple-tree";
let nextId = 0;
export function useSimpleTree(initialData) {
    const [data, setData] = useState(initialData);
    const tree = useMemo(() => new SimpleTree(data), [data]);
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
