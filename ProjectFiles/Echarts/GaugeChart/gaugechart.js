
let chartOptixData = {}
let myChart;
const url = new URL(window.location.href);
const urlLink = url.protocol + "//" + url.hostname + ":" + url.port;
const nodePath = url.searchParams.get('p');
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
        chartOptixData = optixOriginalData.data
        let option = {
          title: {
            text: 'GaugeChart',
            left: 'center',
            textStyle: {
              color: '#333'
            }
          },
          series: [
            {
              type: 'gauge',
              progress: {
                show: true,
                width: 18,
                color: [
                  [0.3, '#67e0e3'],
                  [0.7, '#37a2da'],
                  [1, '#fd666d']
                ]
              },
              axisLine: {
                lineStyle: {
                  width: 18
                }
              },
              axisTick: {
                show: false
              },
              splitLine: {
                length: 15,
                lineStyle: {
                  width: 2,
                  color: '#999'
                }
              },
              axisLabel: {
                distance: 25,
                color: '#999',
                fontSize: 20
              },
              pointer: {
                itemStyle: {
                  color: 'auto'
                }
              },
              anchor: {
                show: true,
                showAbove: true,
                size: 25,
                itemStyle: {
                  borderWidth: 10
                }
              },
              title: {
                show: false
              },
              detail: {
                valueAnimation: true,
                fontSize: 80,
                offsetCenter: [0, '70%']
              },
              data: [
                {
                  value: 70
                }
              ]
            }
          ]
        };
        const title = findItem(chartOptixData, 'title');
        option.title.text = title ? title.Value.Value : 'Title';
        const titleFontSize = findItem(chartOptixData, 'titleFontSize');
        option.title.textStyle.fontSize = titleFontSize ? titleFontSize.Value.Value : 16;
        const titleColor = findItem(chartOptixData, 'titleColor');
        option.title.textStyle.color = titleColor ? decimalToHexColor(titleColor.Value.Value) : "#000000";
        const chartBackground = findItem(chartOptixData, 'background');
        if (chartBackground) {
          const chartBackgroundElement = document.querySelector('html.chartbackground');
          if (chartBackgroundElement) {
            chartBackgroundElement.style.backgroundColor = decimalToHexColor(chartBackground.Value.Value);
          }
        }
        const refreshIntervalItem = findItem(chartOptixData, 'refreshInterval');
        const refreshIntervalData = refreshIntervalItem ? refreshIntervalItem.Value.Value : 0;
        let interval = parseFloat(refreshIntervalData);
        if (interval > 0)
          setTimeout(() => {
            fetchAndParseData(`${urlLink}/fetchData?p=${nodePath}`).then(options => {
              myChart.setOption(options);
            })
          }, interval);

        const subData = findSubItem(chartOptixData)
        option.series = []

        for (property in subData) {
          let subDataItem = subData[property]
          option.series.push({
            data: [
              {
                value: subDataItem.data || 0
              }
            ],
            type: 'gauge',
            center: subDataItem?.center || ['50%', '50%'],
            radius: subDataItem?.radius || '100%',
            startAngle: subDataItem?.startAngle || 0,
            endAngle: subDataItem?.endAngle || 0,
            min: subDataItem?.minValue || 0,
            max: subDataItem?.maxValue || 60,
            splitNumber: subDataItem?.splitNumber || 12,
            itemStyle: {
              color: subDataItem?.progressColor && decimalToHexColor(subDataItem.progressColor) || '#67e0e3',
            },
            progress: {
              show: subDataItem?.showProgress,
              width: subDataItem?.progressWidth || 18,
              roundCap: subDataItem?.progressRoundCap
            },
            axisLine: {
              roundCap: subDataItem?.axisLineRoundCap,
              lineStyle: {
                width: subDataItem?.axisLineWidth || 18,
                color: subDataItem?.axisLineColor && subDataItem.axisLineColor.map((color, index) => {
                  let ratio = (index + 1) / subDataItem.axisLineColor.length;
                  return [ratio, decimalToHexColor(color)];
                })
              },

            },
            axisTick: {
              show: subDataItem?.showAxisTick,
              distance: subDataItem?.axisTickDistance || -30,
              length: subDataItem?.axisTickLength || 8,
              splitNumber: subDataItem?.axisTickSplitNumber || 2,
              lineStyle: {
                width: subDataItem?.axisTickWidth,
                color: subDataItem?.axisTickColor && decimalToHexColor(subDataItem.axisTickColor) || '#999'
              }
            },
            splitLine: {
              show: subDataItem?.showSplitLine,
              distance: subDataItem?.splitLineDistance || -52,
              length: subDataItem?.splitLineLength || 20,
              lineStyle: {
                width: subDataItem?.splitLineWidth || 3,
                color: subDataItem?.splitLineColor && decimalToHexColor(subDataItem.splitLineColor) || '#999'
              }
            },
            axisLabel: {
              distance: subDataItem?.axisLabelDistance || 25,
              color: subDataItem?.axisLabelColor && decimalToHexColor(subDataItem.axisLabelColor) || '#999',
              fontSize: subDataItem?.axisLabelFontSize || 20
            },
            pointer: {
              show: subDataItem?.showPointer,
              width: subDataItem?.pointWidth || 16,
              itemStyle: {
                color: subDataItem?.pointColor && decimalToHexColor(subDataItem.pointColor)
              }
            },
            anchor: {
              show: subDataItem?.showAnchor,
              showAbove: true,
              size: subDataItem?.anchorSize || 25,
              itemStyle: {
                borderWidth: subDataItem?.anchorBorderWidth || 10
              }
            },
            title: {
              show: false
            },
            detail: {
              show: subDataItem?.showCurrentValue,
              valueAnimation: true,
              fontSize: subDataItem?.currentValueFontSize || 80,
              offsetCenter: subDataItem?.currentValuePosition || [0, '50%'],
              formatter: `{value} ${subDataItem?.currentValueUnit}`,
              color: decimalToHexColor(subDataItem?.currentValueColor)
            },
          })
        }
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

document.addEventListener('DOMContentLoaded', function () {
  fetchAndParseData(`${urlLink}/fetchData?p=${nodePath}`).then(options => {
    myChart = echarts.init(document.getElementById('chart'));
    myChart.setOption(options);
  }
  )
});

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
