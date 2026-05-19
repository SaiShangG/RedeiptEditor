
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
            text: 'Piechart',
            left: 'center',
            textStyle: {
              color: '#333'
            }
          },
          legend: {
            orient: 'horizontal',
            left: 'center',
            top: "8%",
            textStyle: {
              color: '#333'
            }
          },
          tooltip: {
            trigger: 'item'
          },

          series: [
            {
              type: 'pie',
              label: {
                show: true,
              },
              labelLine: {
                show: true
              },
              data: [
                { value: 1048, name: 'Search Engine' },
                { value: 735, name: 'Direct' },
                { value: 580, name: 'Email' },
                { value: 484, name: 'Union Ads' },
                { value: 300, name: 'Video Ads' }
              ],
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
        var data = []
        var pieChartColorData = []
        var labels = []
        let pieDataItem = {}

        for (key in subData) {
          pieDataItem = subData[key]
          data = pieDataItem.data
          pieChartColorData = pieDataItem?.pieColorConfig
          labels = pieDataItem.labels
        }
        let pieData = data.map((value, index) => ({
          value: value,
          name: labels[index]
        }));


        option.series.push({
          data: pieData,
          type: 'pie',
          radius: pieDataItem?.radius ? pieDataItem?.radius : ['0%', '50%'],
          center: pieDataItem?.center ? pieDataItem?.center : ['50%', '50%'],
          label: {
            show: pieDataItem?.showLabel ?? true,
            formatter: '{b}: {c} ({d}%)',
            color: pieDataItem?.labelColor ? decimalToHexColor(pieDataItem?.labelColor) : '#333'
          },
          labelLine: {
            show: pieDataItem?.showLabelLine ?? true,
          },
          avoidLabelOverlap: pieDataItem?.avoidLabelOverlap ?? true,
          padAngle: pieDataItem?.padAngle ? pieDataItem?.padAngle : 0,
          startAngle: pieDataItem?.startAngle ? pieDataItem?.startAngle : 0,
          endAngle: pieDataItem?.endAngle ? pieDataItem?.endAngle : 360,
          itemStyle: {
            normal: {
              color: function (params) {
                var colorList = pieChartColorData ? pieChartColorData?.map(color => decimalToHexColor(color)) :
                  [
                    '#C1232B', '#B5C334', '#FCCE10', '#E87C25', '#27727B',
                    '#FE8463', '#9BCA63', '#FAD860', '#F3A43B', '#60C0DD',
                    '#D7504B', '#C6E579', '#F4E001', '#F0805A', '#26C0C0'
                  ]
                return colorList[params.dataIndex]
              },
              borderRadius: pieDataItem?.borderRadius ? pieDataItem?.borderRadius : 0,
              borderColor: pieDataItem?.borderColor ? decimalToHexColor(pieDataItem?.borderColor) : '#000',
              borderWidth: pieDataItem?.borderWidth ? pieDataItem?.borderWidth : 0,
            }
          },
        })
        if (pieDataItem.labelColor) {
          option.legend.textStyle.color = decimalToHexColor(pieDataItem.labelColor)
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
