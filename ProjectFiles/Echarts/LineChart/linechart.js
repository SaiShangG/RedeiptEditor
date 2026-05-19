
let lineChartOptixData = {};
let barChartOptixData = {}
let myChart;
const url = new URL(window.location.href);
const urlLink = url.protocol + "//" + url.hostname + ":" + url.port;
const nodePath = url.searchParams.get('p');
const lineTypeMap = {
  0: 'line',
  1: 'bar',
  2: 'line'
}
const axisSplitLineTypeMap = {
  0: 'solid',
  1: 'dashed',
  2: 'dotted'
}
async function fetchAndParseData(url) {
  return fetch(url)
    .then(response => {
      if (!response.ok) {
        throw new Error('Network response is not ok.');
      }
      return response.text();
    })
    .then(data => {
      optixOriginalData = JSON.parse(data)
      if (Object.keys(optixOriginalData)[0] === 'data') {
        lineChartOptixData = optixOriginalData.data
        // Chart's default and inital option configuration
        let option = {
          legend: {
            textStyle: {
              color: '#ffffff'
            }
          },
          tooltip: {
            trigger: 'axis',
          },
          toolbox: {
            show: true,
            feature: {
              dataZoom: {
                yAxisIndex: 'none'
              },
              dataView: { readOnly: false },
              restore: {},
              saveAsImage: {}
            }
          },
          xAxis: {
            data: ['A', 'B', 'C', 'D', 'E'],
            axisLabel: {
              color: '#fc0327'
            },
            axisLine: {
              lineStyle: {
                color: '#000000'
              }
            },
            splitLine: {
              lineStyle: {
                color: '#ccc',
                type: 'dashed'
              }
            }
          },
          yAxis: {
            axisLabel: {
              color: '#fc0327'
            }, axisLine: {
              lineStyle: {
                color: '#000000'
              }
            },
            splitLine: {
              lineStyle: {
                color: '#ccc',
                type: 'dashed'
              }
            }
          },
          series: [
            {
              data: [10, 22, 28, 23, 19],
              type: 'line',
              lineStyle: {
                normal: {
                  color: 'green',
                  width: 4,
                  type: 'dashed'
                }
              }
            }
          ]
        };
        const xAxisTypeItem = findItem(lineChartOptixData, 'xAxisType');
        const xAxisType = xAxisTypeItem ? xAxisTypeItem.Value.Value : 0;
        option.xAxis.type = xAxisType === 0 ? 'category' : 'value';
        const xAxisDataItem = findItem(lineChartOptixData, 'xAxisData');
        const xAxisData = xAxisDataItem ? xAxisDataItem.Value.Value : [];
        option.xAxis.data = xAxisData;
        const axisFontColor = findItem(lineChartOptixData, 'axisFontColor');
        const axisFontColorData = axisFontColor ? axisFontColor.Value.Value : undefined;
        if (axisFontColorData) {

          option.xAxis.axisLabel.color = decimalToHexColor(axisFontColorData);
          option.yAxis.axisLabel.color = decimalToHexColor(axisFontColorData);
        }
        const axisLineColor = findItem(lineChartOptixData, 'axisLineColor');
        const axisLineColorData = axisLineColor ? axisLineColor.Value.Value : undefined;
        if (axisLineColorData) {

          option.xAxis.axisLine.lineStyle.color = decimalToHexColor(axisLineColorData);
          option.yAxis.axisLine.lineStyle.color = decimalToHexColor(axisLineColorData);
        }
        const axisSplitLineColor = findItem(lineChartOptixData, 'axisSplitLineColor');
        const axisSplitLineColorData = axisSplitLineColor ? axisSplitLineColor.Value.Value : undefined;
        if (axisSplitLineColorData) {

          option.xAxis.splitLine.lineStyle.color = decimalToHexColor(axisSplitLineColorData);
          option.yAxis.splitLine.lineStyle.color = decimalToHexColor(axisSplitLineColorData);
        }
        const axisSplitLineType = findItem(lineChartOptixData, 'axisSplitLineType');
        const axisSplitLineTypeData = axisSplitLineType ? axisSplitLineType.Value.Value : undefined;
        if (axisSplitLineTypeData) {

          option.xAxis.splitLine.lineStyle.type = axisSplitLineTypeMap[axisSplitLineTypeData];
          option.yAxis.splitLine.lineStyle.type = axisSplitLineTypeMap[axisSplitLineTypeData];
        }

        const toolboxFeature = findItem(lineChartOptixData, 'toolbox');
        option.toolbox = toolboxFeature ? generateToolboxConfig(toolboxFeature.Value.Value, option.toolbox.feature) : {}
        const chartBackground = findItem(lineChartOptixData, 'background');
        if (chartBackground) {
          const chartBackgroundElement = document.querySelector('html.chartbackground');
          if (chartBackgroundElement) {
            chartBackgroundElement.style.backgroundColor = decimalToHexColor(chartBackground.Value.Value);
          }
        }
        const refreshIntervalItem = findItem(lineChartOptixData, 'refreshInterval');
        const refreshIntervalData = refreshIntervalItem ? refreshIntervalItem.Value.Value : 0;
        let interval = parseFloat(refreshIntervalData);
        if (interval > 0)
          setTimeout(() => {
            fetchAndParseData(`${urlLink}/fetchData?p=${nodePath}`).then(options => {
              myChart.setOption(options);
            })
          }, interval);
        const subLine = findSubItem(lineChartOptixData)
        option.series = []
        for (lineName in subLine) {
          let lineDataItem = subLine[lineName]
          option.series.push({
            data: lineDataItem.data,
            type: lineTypeMap[lineDataItem.lineType] || 'line',
            barWidth: lineDataItem?.bandWidth || '50%',
            smooth: lineDataItem?.smooth,
            name: lineDataItem.label ? lineDataItem.label : '',
            lineStyle: {
              normal: {
                color: lineDataItem.lineColor ? decimalToHexColor(lineDataItem.lineColor) : '',
                width: lineDataItem.width ? lineDataItem.width : 2,
                type: axisSplitLineTypeMap[lineDataItem.lineType]
              }
            },
            itemStyle: {
              color: lineDataItem.areaColor && getGradientColor(decimalToHexColor(lineDataItem.areaColor[0]), decimalToHexColor(lineDataItem.areaColor[1])),
            },
            areaStyle: lineDataItem?.areaStyle ? {} : undefined,
            emphasis: lineDataItem?.emphasis ? {
              focus: 'series'
            } : {},
            markPoint: lineDataItem.markPoint ? {
              data: [
                { type: 'max', name: 'Max' },
                { type: 'min', name: 'Min' }
              ]
            } : {},
            markLine: lineDataItem.markLine ? {
              data: [{ type: 'average', name: 'Avg' }]
            } : {},


          })
          if (lineDataItem.labelColor) {
            option.legend.textStyle.color = lineDataItem.labelColor
          }
        }
        console.log("option", option)
        return option
      }

    })
    .catch(error => {
      console.error('请求失败:', error);
    });
}

function findItem(data, relativePath) {
  for (let i = 0; i < data.length; i++) {
    if (data[i].RelativePath === relativePath) {
      return data[i];
    }
  }
  return null;
}

function findSubItem(data) {
  let itemData = {}
  for (let i = 0; i < data.length; i++) {
    let name = data[i].RelativePath.split('/')
    if (name.length == 2) {
      if (itemData[name[0]] === undefined)
        itemData[name[0]] = {}
      itemData[name[0]][name[1]] = data[i].Value.Value
    }
  }
  return itemData
}
function decimalToHexColor(decimal) {

  if (typeof decimal !== 'number') {
    throw new Error('Input must be a number');
  }


  const r = (decimal >> 16) & 0xFF;
  const g = (decimal >> 8) & 0xFF;
  const b = decimal & 0xFF;


  const toHex = (value) => {
    const hex = value.toString(16).padStart(2, '0').toUpperCase();
    return hex;
  };


  return `#${toHex(r)}${toHex(g)}${toHex(b)}`;
}

function generateToolboxConfig(features, featureConfigurations) {
  const toolboxConfig = {
    show: true,
    feature: {}
  };

  features.forEach(feature => {
    if (feature && featureConfigurations[feature]) {
      toolboxConfig.feature[feature] = featureConfigurations[feature];
    }
  });

  return toolboxConfig;
}

function getGradientColor(startColor, endColor) {
  return {
    type: 'linear',
    x: 0,
    y: 0,
    x2: 0,
    y2: 1,
    colorStops: [
      { offset: 0, color: startColor }, // 0% 处的颜色
      { offset: 1, color: endColor }  // 100% 处的颜色
    ]
  };
}

document.addEventListener('DOMContentLoaded', function () {


  fetchAndParseData(`${urlLink}/fetchData?p=${nodePath}`).then(options => {

    myChart = echarts.init(document.getElementById('chart'));
    myChart.setOption(options);

  }
  )

});
