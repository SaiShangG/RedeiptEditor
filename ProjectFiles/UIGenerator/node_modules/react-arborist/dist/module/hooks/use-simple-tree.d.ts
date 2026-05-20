import { CreateHandler, DeleteHandler, MoveHandler, RenameHandler } from "../types/handlers";
export type SimpleTreeData = {
    id: string;
    name: string;
    children?: SimpleTreeData[];
};
export declare function useSimpleTree<T>(initialData: readonly T[]): readonly [readonly T[], {
    onMove: MoveHandler<T>;
    onRename: RenameHandler<T>;
    onCreate: CreateHandler<T>;
    onDelete: DeleteHandler<T>;
}];
