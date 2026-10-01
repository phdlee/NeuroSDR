/**
 * @typedef {Object} RadioRemoteSnapshot
 * @property {boolean} running
 * @property {string} source
 * @property {number} frequencyHz
 * @property {number} rfCenterHz
 * @property {number} viewCenterHz
 * @property {number} viewBandwidthHz
 * @property {string} mode
 * @property {number} filterBandwidthHz
 * @property {number} gainPercent
 * @property {number} signalDb
 * @property {number} volume1
 * @property {number} volume2
 * @property {boolean} squelch1Enabled
 * @property {number} squelch1Threshold
 * @property {boolean} squelch2Enabled
 * @property {number} squelch2Threshold
 * @property {boolean} squelch1Open
 * @property {boolean} squelch2Open
 * @property {boolean} audio2Enabled
 * @property {{id:string,name:string,typeId:string}[]|string[]} activeAfPlugins
 * @property {string[]} availableSources
 * @property {string[]} availableModes
 * @property {string} status
 * @property {string} selectedSceneId
 * @property {string} selectedSceneName
 * @property {string} selectedChannelId
 * @property {{id:string,name:string}[]} scenes
 * @property {{id:string,label:string,frequencyHz:number,mode:string,global:boolean}[]} channels
 * @property {{id:string,name:string,frequencyHz:number,mode:string,bandwidthHz:number}[]} subVfos
 * @property {{id:string,name:string,frequencyHz:number,mode:string}[]} bands
 * @property {number[]} bandwidthPresets
 */

/**
 * @typedef {Object} SpectrumRemoteFrame
 * @property {number} centerHz
 * @property {number} spanHz
 * @property {number} tunedHz
 * @property {number} filterHz
 * @property {string} mode
 * @property {number[]} levels
 */

/**
 * @typedef {Object} AfPluginRemoteEvent
 * @property {string} pluginId
 * @property {string} pluginName
 * @property {string} kind
 * @property {string} text
 * @property {number} frequencyHz
 * @property {number} utcTicks
 */

export {};
