export class SimpleTree {
    constructor(data) {
        this.root = createRoot(data);
    }
    get data() {
        var _a, _b;
        return (_b = (_a = this.root.children) === null || _a === void 0 ? void 0 : _a.map((node) => node.data)) !== null && _b !== void 0 ? _b : [];
    }
    create(args) {
        const parent = args.parentId ? this.find(args.parentId) : this.root;
        if (!parent)
            return null;
        parent.addChild(args.data, args.index);
    }
    move(args) {
        const src = this.find(args.id);
        const parent = args.parentId ? this.find(args.parentId) : this.root;
        if (!src || !parent)
            return;
        parent.addChild(src.data, args.index);
        src.drop();
    }
    update(args) {
        const node = this.find(args.id);
        if (node)
            node.update(args.changes);
    }
    drop(args) {
        const node = this.find(args.id);
        if (node)
            node.drop();
    }
    find(id, node = this.root) {
        if (!node)
            return null;
        if (node.id === id)
            return node;
        if (node.children) {
            for (let child of node.children) {
                const found = this.find(id, child);
                if (found)
                    return found;
            }
            return null;
        }
        return null;
    }
}
function createRoot(data) {
    const root = new SimpleNode({ id: "ROOT" }, null);
    root.children = data.map((d) => createNode(d, root));
    return root;
}
function createNode(data, parent) {
    const node = new SimpleNode(data, parent);
    if (data.children)
        node.children = data.children.map((d) => createNode(d, node));
    return node;
}
class SimpleNode {
    constructor(data, parent) {
        this.data = data;
        this.parent = parent;
        this.id = data.id;
    }
    hasParent() {
        return !!this.parent;
    }
    get childIndex() {
        return this.hasParent() ? this.parent.children.indexOf(this) : -1;
    }
    addChild(data, index) {
        var _a, _b;
        const node = createNode(data, this);
        this.children = (_a = this.children) !== null && _a !== void 0 ? _a : [];
        this.children.splice(index, 0, node);
        this.data.children = (_b = this.data.children) !== null && _b !== void 0 ? _b : [];
        this.data.children.splice(index, 0, data);
    }
    removeChild(index) {
        var _a, _b;
        (_a = this.children) === null || _a === void 0 ? void 0 : _a.splice(index, 1);
        (_b = this.data.children) === null || _b === void 0 ? void 0 : _b.splice(index, 1);
    }
    update(changes) {
        if (this.hasParent()) {
            const i = this.childIndex;
            this.parent.addChild(Object.assign(Object.assign({}, this.data), changes), i);
            this.drop();
        }
    }
    drop() {
        if (this.hasParent())
            this.parent.removeChild(this.childIndex);
    }
}
