import { ConnectDragSource } from "react-dnd";
import { NodeApi } from "../interfaces/node-api";
export declare function useDragHook<T>(node: NodeApi<T>): ConnectDragSource;
