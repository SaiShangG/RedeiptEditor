type SimpleData = {
    id: string;
    name: string;
    children?: SimpleData[];
};
export declare class SimpleTree<T extends SimpleData> {
    root: SimpleNode<T>;
    constructor(data: T[]);
    get data(): T[];
    create(args: {
        parentId: string | null;
        index: number;
        data: T;
    }): null | undefined;
    move(args: {
        id: string;
        parentId: string | null;
        index: number;
    }): void;
    update(args: {
        id: string;
        changes: Partial<T>;
    }): void;
    drop(args: {
        id: string;
    }): void;
    find(id: string, node?: SimpleNode<T>): SimpleNode<T> | null;
}
declare class SimpleNode<T extends SimpleData> {
    data: T;
    parent: SimpleNode<T> | null;
    id: string;
    children?: SimpleNode<T>[];
    constructor(data: T, parent: SimpleNode<T> | null);
    hasParent(): this is this & {
        parent: SimpleNode<T>;
    };
    get childIndex(): number;
    addChild(data: T, index: number): void;
    removeChild(index: number): void;
    update(changes: Partial<T>): void;
    drop(): void;
}
export {};
