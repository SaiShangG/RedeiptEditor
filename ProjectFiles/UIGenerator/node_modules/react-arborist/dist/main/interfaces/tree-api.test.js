"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
const redux_1 = require("redux");
const root_reducer_1 = require("../state/root-reducer");
const tree_api_1 = require("./tree-api");
function setupApi(props) {
    const store = (0, redux_1.createStore)(root_reducer_1.rootReducer);
    return new tree_api_1.TreeApi(store, props, { current: null }, { current: null });
}
test("tree.canDrop()", () => {
    expect(setupApi({ disableDrop: true }).canDrop()).toBe(false);
    expect(setupApi({ disableDrop: () => false }).canDrop()).toBe(true);
    expect(setupApi({ disableDrop: false }).canDrop()).toBe(true);
});
