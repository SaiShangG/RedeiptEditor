一个布局编辑器。
1. 用于配置用户可配置的UI
2. 配置最总输出为JSON，例如

{
  "version": 3,
  "description": "Single-template test layout aligned to UDT_Phase. Two test configs per area.",
  "sections": [
    {
    }
    
        

3. 一个实时的画布，用于实时预览
4. 一个属性面板，用于调整参数值
5. 一个树，用于显示对象结构

定义对象如下：

1. 根容器，最外层的对象，

"id": "ParaPanel1",                 //自动生成
"panelType": "PhaseParasPanel",     //预设，用户参数
"title": "Phase Parameters Area",   //预设，用户参数
"rowLayoutPath": "VL/HL",           //预设，用户参数
"rowLayoutHorizontalGap": 8,        //预设，用户参数
"sectionVerticalGap": 8,            //预设，用户参数
"items": []                         //子对象。

2. 子对象，跟容器里放的对象：

"id": "Para0",                      //自动生成
"widgetType": "PhaseSinglePara",    //预设，用户参数
"label": "Para1",                   //预设，用户参数
"bind": {
    "uiProperty": "Text",           //预设，用户参数
    "sourceTagPath": "PP.FixedSetPointValue[0]" //预设，用户参数
}
      

