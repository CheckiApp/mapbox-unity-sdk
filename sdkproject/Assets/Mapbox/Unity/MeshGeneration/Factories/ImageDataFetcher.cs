﻿using Mapbox.Map;
using Mapbox.Unity.MeshGeneration.Data;
using Mapbox.Unity.MeshGeneration.Enums;
using System;
using System.Collections;
using System.Collections.Generic;
using Mapbox.Platform;
using Mapbox.Platform.Cache;
using Mapbox.Unity;
using Mapbox.Unity.Utilities;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

public class ImageDataFetcher : DataFetcher
{
	public Action<UnityTile, RasterTile> TextureReceived = (t, s) => { };
	public Action<UnityTile, RasterTile, TileErrorEventArgs> FetchingError = (t, r, s) => { };

	public ImageDataFetcher(IFileSource fileSource) : base(fileSource)
	{

	}

	public override void FetchData(DataFetcherParameters parameters)
	{
		var imageDataParameters = parameters as ImageDataFetcherParameters;
		if(imageDataParameters == null)
		{
			return;
		}

		FetchData(imageDataParameters.tilesetId, imageDataParameters.canonicalTileId, imageDataParameters.useRetina, imageDataParameters.tile);
	}

	public virtual void FetchData(RasterTile tile, string tilesetId, CanonicalTileId tileId, bool useRetina, UnityTile unityTile = null)
	{
		//MemoryCacheCheck
		var textureItem = MapboxAccess.Instance.CacheManager.GetTextureItemFromMemory(tilesetId, tileId);
		if (textureItem != null)
		{
			tile.SetTextureFromCache(textureItem.Texture2D);
#if UNITY_EDITOR
			tile.FromCache = CacheType.MemoryCache;
#endif
			TextureReceived(unityTile, tile);
			return;
		}

		//FileCacheCheck
		if (MapboxAccess.Instance.CacheManager.TextureFileExists(tilesetId, tileId)) //not in memory, check file cache
		{
			MapboxAccess.Instance.CacheManager.GetTextureItemFromFile(tilesetId, tileId, (textureCacheItem) =>
			{
				if (unityTile != null && unityTile.CanonicalTileId != tileId)
				{
					//this means tile object is recycled and reused. Returned data doesn't belong to this tile but probably the previous one. So we're trashing it.
					return;
				}

				//even though we just checked file exists, system couldn't find&load it
				//this shouldn't happen frequently, only in some corner cases
				//one possibility might be file being pruned due to hitting cache limit
				//after that first check few lines above and actual loading (loading is scheduled and delayed so it's not in same frame)
				if (textureCacheItem != null)
				{
					tile.SetTextureFromCache(textureCacheItem.Texture2D);
#if UNITY_EDITOR
					tile.FromCache = CacheType.FileCache;
#endif
					//do we need these for live products or are they only for debugging?
					tile.ETag = textureCacheItem.ETag;
					if (textureCacheItem.ExpirationDate.HasValue)
					{
						tile.ExpirationDate = textureCacheItem.ExpirationDate.Value;
					}

					TextureReceived(unityTile, tile);

					//after returning what we already have
					//check if it's out of date, if so check server for update
					if (textureCacheItem.ExpirationDate < DateTime.Now)
					{
						EnqueueForFetching(new FetchInfo(tileId, tilesetId, tile, textureCacheItem.ETag)
						{
							Callback = () => { FetchingCallback(tileId, tile, unityTile); }
						});
					}
				}
				else
				{
					EnqueueForFetching(new FetchInfo(tileId, tilesetId, tile, String.Empty)
					{
						Callback = () => { FetchingCallback(tileId, tile, unityTile); }
					});
				}
			});

			return;
		}

		//not in cache so web request
		//CreateWebRequest(tilesetId, tileId, useRetina, String.Empty, unityTile);
		EnqueueForFetching(new FetchInfo(tileId, tilesetId, tile, String.Empty)
		{
			Callback = () => { FetchingCallback(tileId, tile, unityTile); }
		});
	}

	//tile here should be totally optional and used only not to have keep a dictionary in terrain factory base
	public void FetchData(string tilesetId, CanonicalTileId tileId, bool useRetina, UnityTile unityTile = null)
	{
		//MemoryCacheCheck
		var textureItem = MapboxAccess.Instance.CacheManager.GetTextureItemFromMemory(tilesetId, tileId);
		if (textureItem != null)
		{
			var rasterTile = new RasterTile(tileId, tilesetId);
			rasterTile.SetTextureFromCache(textureItem.Texture2D);
			if (unityTile != null) { unityTile.AddTile(rasterTile); }

			TextureReceived(unityTile, rasterTile);
			return;
		}

		//FileCacheCheck
		if (MapboxAccess.Instance.CacheManager.TextureFileExists(tilesetId, tileId)) //not in memory, check file cache
		{
			MapboxAccess.Instance.CacheManager.GetTextureItemFromFile(tilesetId, tileId, (textureCacheItem) =>
			{
				if (unityTile != null && unityTile.CanonicalTileId != tileId)
				{
					//this means tile object is recycled and reused. Returned data doesn't belong to this tile but probably the previous one. So we're trashing it.
					return;
				}

				//even though we just checked file exists, system couldn't find&load it
				//this shouldn't happen frequently, only in some corner cases
				//one possibility might be file being pruned due to hitting cache limit
				//after that first check few lines above and actual loading (loading is scheduled and delayed so it's not in same frame)
				if (textureCacheItem != null)
				{
					var rasterTile = new RasterTile(tileId, tilesetId);
					rasterTile.SetTextureFromCache(textureCacheItem.Texture2D);
					rasterTile.ETag = textureCacheItem.ETag;
					rasterTile.ExpirationDate = textureCacheItem.ExpirationDate.Value;
					if (unityTile != null) { unityTile.AddTile(rasterTile); }

					TextureReceived(unityTile, rasterTile);

					//after returning what we already have
					//check if it's out of date, if so check server for update
					if (textureCacheItem.ExpirationDate < DateTime.Now)
					{
						CreateWebRequest(tilesetId, tileId, useRetina, textureCacheItem.ETag, unityTile);
					}
				}
				else
				{
					CreateWebRequest(tilesetId, tileId, useRetina, String.Empty, unityTile);
				}
			});

			return;
		}

		//not in cache so web request
		CreateWebRequest(tilesetId, tileId, useRetina, String.Empty, unityTile);
	}

	protected virtual void CreateWebRequest(string tilesetId, CanonicalTileId tileId, bool useRetina, string etag, UnityTile unityTile = null)
	{
		RasterTile rasterTile;
		//`starts with` is weak and string operations are slow
		//but caching type and using Activator.CreateInstance (or caching func and calling it)  is even slower
		if (tilesetId.StartsWith("mapbox://", StringComparison.Ordinal))
		{
			rasterTile = useRetina ? new RetinaRasterTile(tileId, tilesetId) : new RasterTile(tileId, tilesetId);
		}
		else
		{
			rasterTile = useRetina ? new ClassicRetinaRasterTile(tileId, tilesetId) : new ClassicRasterTile(tileId, tilesetId);
		}

		if (unityTile != null)
		{
			unityTile.AddTile(rasterTile);
		}

		EnqueueForFetching(new FetchInfo(tileId, tilesetId, rasterTile, etag)
		{
			Callback = () => { FetchingCallback(tileId, rasterTile, unityTile); }
		});
	}

	protected virtual void FetchingCallback(CanonicalTileId tileId, RasterTile rasterTile, UnityTile unityTile = null)
	{
		if (unityTile != null && !unityTile.ContainsDataTile(rasterTile))
		{
			//rasterTile.Clear();
			//this means tile object is recycled and reused. Returned data doesn't belong to this tile but probably the previous one. So we're trashing it.
			return;
		}

		if (rasterTile.HasError)
		{
			FetchingError(unityTile, rasterTile, new TileErrorEventArgs(tileId, rasterTile.GetType(), unityTile, rasterTile.Exceptions));
		}
		else
		{

			rasterTile.ExtractTextureFromRequest();

#if UNITY_EDITOR
			if (rasterTile.Texture2D != null)
			{
				rasterTile.Texture2D.name = string.Format("{0}_{1}", tileId.ToString(), rasterTile.TilesetId);
			}
#endif
			MapboxAccess.Instance.CacheManager.AddTextureItem(
				rasterTile.TilesetId,
				rasterTile.Id,
				new TextureCacheItem()
				{
					TileId = tileId,
					TilesetId = rasterTile.TilesetId,
					From = rasterTile.FromCache,
					ETag = rasterTile.ETag,
					Data = rasterTile.Data,
					ExpirationDate = rasterTile.ExpirationDate,
					Texture2D = rasterTile.Texture2D
				},
				true);

			if (rasterTile.StatusCode != 304) //NOT MODIFIED
			{
				TextureReceived(unityTile, rasterTile);
			}
		}
	}
}

public class BaseImageDataFetcher : ImageDataFetcher
{
	public BaseImageDataFetcher(IFileSource fileSource) : base(fileSource)
	{

	}

	public void FetchData(RasterTile tile, string tilesetId, CanonicalTileId tileId, bool useRetina, UnityTile unityTile = null)
	{
		//MemoryCacheCheck
		var textureItem = MapboxAccess.Instance.CacheManager.GetTextureItemFromMemory(tilesetId, tileId);
		if (textureItem != null)
		{
			var rasterTile = new RasterTile(tileId, tilesetId);
			rasterTile.SetTextureFromCache(textureItem.Texture2D);
#if UNITY_EDITOR
			rasterTile.FromCache = CacheType.MemoryCache;
#endif
			TextureReceived(unityTile, rasterTile);
			return;
		}

		//FileCacheCheck
		if (MapboxAccess.Instance.CacheManager.TextureFileExists(tilesetId, tileId)) //not in memory, check file cache
		{
			MapboxAccess.Instance.CacheManager.GetTextureItemFromFile(tilesetId, tileId, (textureCacheItem) =>
			{
				//even though we just checked file exists, system couldn't find&load it
				//this shouldn't happen frequently, only in some corner cases
				//one possibility might be file being pruned due to hitting cache limit
				//after that first check few lines above and actual loading (loading is scheduled and delayed so it's not in same frame)
				if (textureCacheItem != null)
				{
					var rasterTile = new RasterTile(tileId, tilesetId);
					rasterTile.SetTextureFromCache(textureCacheItem.Texture2D);
#if UNITY_EDITOR
					rasterTile.FromCache = CacheType.FileCache;
#endif
					rasterTile.ETag = textureCacheItem.ETag;
					rasterTile.ExpirationDate = textureCacheItem.ExpirationDate.Value;
					TextureReceived(unityTile, rasterTile);
					MapboxAccess.Instance.CacheManager.MarkFixed(tileId, tilesetId);

					//after returning what we already have
					//check if it's out of date, if so check server for update
					if (textureCacheItem.ExpirationDate < DateTime.Now)
					{
						CreateWebRequest(tilesetId, tileId, useRetina, textureCacheItem.ETag, unityTile);
					}
				}
				else
				{
					CreateWebRequest(tilesetId, tileId, useRetina, String.Empty, unityTile);
				}
			});

			return;
		}

		//not in cache so web request
		EnqueueForFetching(new FetchInfo(tileId, tilesetId, tile, String.Empty)
		{
			Callback = () => { FetchingCallback(tileId, tile, unityTile); }
		});
	}

	protected override void FetchingCallback(CanonicalTileId tileId, RasterTile rasterTile, UnityTile unityTile = null)
	{
		base.FetchingCallback(tileId, rasterTile, unityTile);
#if UNITY_EDITOR
		if (rasterTile.Texture2D != null)
		{
			rasterTile.Texture2D.name += "_fallbackImage";
		}
#endif
		MapboxAccess.Instance.CacheManager.MarkFixed(rasterTile.Id, rasterTile.TilesetId);
	}
}

public class ImageDataFetcherParameters : DataFetcherParameters
{
	public bool useRetina = true;
}
